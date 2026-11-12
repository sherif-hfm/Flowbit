# TASK-05A: Shared automatic multi-instance core

[Parent TASK-05](task-05-multi-instance-automatic-activities.md) · [Enhancement backlog](README.md#task-05-delivery-subtasks) · [Documentation home](../index.md)

| Field | Value |
| --- | --- |
| Status | In progress |
| Category | Shared runtime foundation |
| Effort | L |
| Priority | Required foundation for TASK-05 |
| Parent | [TASK-05](task-05-multi-instance-automatic-activities.md) |
| Depends on | None |
| Enables | [TASK-05B service](task-05b-multi-instance-service-activities.md), [TASK-05C script](task-05c-multi-instance-script-activities.md), [TASK-05D call](task-05d-multi-instance-call-activities.md) |

## Summary

Build one persisted automatic-item lifecycle shared by service, script, and call activities. This page owns the common workflow JSON, input snapshots, local and shared-variable output contracts, admission, aggregation, persistence, fences, whole-body error/timer/conditional boundaries, item APIs, and shared editor/runtime presentation. Activity adapters own execution details in their linked subtask plans. All four subtasks are required for the parent first release.

This plan was split from TASK-05 on **2026-11-11**. Its common contracts, persistence, coordinator, query/UI scaffolding and lifecycle hooks are implemented behind closed publication/runtime guards. Automatic multi-instance activities are not available for use until the B/C/D adapters and combined integration gates pass. The split changes ownership, not the agreed feature scope or runtime semantics. The parent records the contract reviews under [Open questions](task-05-multi-instance-automatic-activities.md#open-questions).

A subsequent user-requested scope revision on **2026-11-11** includes shared-variable reads/writes and whole-body timer/conditional boundaries in the first release. The contracts below supersede their earlier deferral and remain the release acceptance contract. Conditional predicates retain the existing instance-variable-only language: accepting a conditional boundary does not introduce cross-instance shared-variable subscriptions.

## First release acceptance criteria

- [ ] One shared model accepts instance or shared typed-array sources, an explicit `afterAll`, explicit local outputs, declared shared output targets, and ordered scalar-JSON aggregates in an instance or shared target while preserving human MI behavior.
- [ ] One parent token/body owns durable automatic items, and at most one body per node is open in an instance; automatic parent items never become human tasks or claim/role-policy rows.
- [ ] Input snapshots, item count, local declarations, result size, and persisted concurrency remain bounded and deterministic. A result that cannot fit is refused at entry, before any item runs.
- [ ] Item completion, refill, aggregate publication, and recovery are idempotent; capacity contention has a durable wake-up, never repeats successful activity work, and opens an incident instead of waiting silently when the body has nothing in flight.
- [ ] All adapters use common activation/item ownership fences, cancellation, loop guarding, ledger accounting, and terminal-state checks.
- [ ] Shared bindings retain catalog/access/type validation, bounded snapshots, exact access plans, ordinal locking and value-version fences. Each item's shared writes commit once with its success; publication-only conflict recovery never repeats successful HTTP, script evaluation, or child work.
- [ ] Interrupting and non-interrupting timer/conditional boundaries work on all three automatic body types, with activation/occurrence fencing, restart-safe subscriptions, queued-entry rules, and cancellation of unfinished items and calls when interrupted.
- [ ] Aggregate reset/publication handles conditional reentrancy; output conflicts recover from saved results without rerunning completed items. A caught item failure stops the body at once, then publishes a partial result before the boundary continues, and a completion failure that retrying cannot change never waits in an incident.
- [ ] Every existing reader of multi-instance executions (by table, entity, or `MultiInstanceExecutionId`), including administrative actions, role management, version change, diagram history, and instance detail, ignores automatic bodies or handles them explicitly; existing human-MI DTO fields never carry an automatic body.
- [ ] Retention and migrations preserve open work, including bodies that outlive the completed-job retention period, and existing human MI, ordinary jobs, and ordinary calls.
- [ ] Paged item reads, progress, node activity, diagram history, permissions, and shared UI work without per-item database queries or fabricated user actions.
- [ ] A release switch keeps automatic MI rejected, and its new editor controls hidden, on every host except Development until the parent's integration step. Shared automated and browser checks pass with the switch opened on the Development test hosts; B/C/D integration gates remain mandatory before the parent can be Done.

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

As reviewed on 2026-11-11, `EnsureMultiInstanceInitializedForTokenAsync` and `BuildMultiInstanceResultAsync` create/read human `user_tasks` and role policies. `MultiInstanceModel` and the existing bulk repository methods supply useful mechanics, but cannot be widened unchanged. Both editor invariants and `WorkflowModelMigrator` currently remove non-user MI. Durable ownership normally assumes one `token.WaitingJobId`, and `CK_execution_tokens_wait_shape` requires every wait state to name a waiting job or timer subscription; the node ledger recognizes only `node` and `userTaskItem`. Ordinary calls already exist but require the separate invocation changes in TASK-05D.

Other current behavior this plan depends on:

- Human MI rejects an empty collection (it requires 1 to `MaxInstances` items) and resets its result variable to `[]` at initialization, so an empty automatic body is new behavior.
- `MultiInstanceModel.CompletionEvaluation` is a non-nullable string that defaults to `afterEach`, and the editor loader fills in `afterEach` too, so an omitted value cannot be told apart from a written one.
- More than fifty non-migration source files reference multi-instance executions by table, entity, or `MultiInstanceExecutionId`, and all assume human work. Some already require a `userTask` node, such as the MI flow and interrupt paths in `WorkflowEngineService.cs`; most SQL readers and UI pages do not. The administrative-action candidate query (`AdministrativeActionCandidateRepository`) lists every active row as an administrative position.
- UI-14 diagram history treats MI as user-task only: `InstanceDiagramHistoryQueryService.IsMultiInstance` requires a `userTask`, and `InstanceDiagramHistoryQueryRepository` groups only `userTaskItem` rows.
- `BuildJobCreate` queues every job of a service or script node in the activity class. A node without an authored `job` policy retries after 10 seconds, 1 minute, and 5 minutes.
- The per-instance open-job limit is twice `Workflow.MultiInstance.MaxInstances`, counted from open job rows (`CountOpenByInstanceAsync`); a finishing job frees its own slot for one replacement. A `callResume` job belongs to the child instance. Exceeding the limit inside a job is an ordinary processing failure (`job_capacity_exceeded`) that consumes attempts; only recurring timer boundaries pause instead.
- A durable REST job snapshots the values its templates reference, including `config.*`, `setting.*`, and `sys.now`, at its first attempt and reuses that snapshot on retries.
- `WorkflowVersionCompatibilityEvaluator.ValidateOpenJobs` compares an open job's `DurableNodeContract` on both versions before `JobDescriptorMatches`. `CanonicalNode` serializes the whole node, `multiInstance` included, so adding, removing, or changing MI on a node with an open job already reports `open_job_contract_changed`.
- The only publication gate, `WorkflowDurableProcessing:PublicationEnabled`, applies to every async, timer, and call definition.
- Retention's completed-job category ages a job by `CompletedAt` even while its instance runs; the Worker initializes that period once from `CompletedJobRetentionDays`, 30 days by default. It protects only leased jobs, jobs with an open incident, and jobs named by `execution_tokens.WaitingJobId`. A `workflow_job_snapshots` row survives only while some job names it, and `CK_workflow_job_snapshots_size` caps it at 1 MiB. Retention never prunes `multi_instance_executions` or `user_tasks`.
- Engine settings are opaque strings that settings management does not validate; each consumer applies its own parse, fallback, and cap rules.
- `WorkflowModelMigrator` clears `cardinalityExpression` and `onePerActor` on collection sources, so a rule that must reject them cannot run on the normalized model.
- `InstanceDetailDto.MultiInstance` and `MultiInstances` carry human progress. `InstanceDetail.razor` calls the human interrupt-flows route for a sole active execution, and while any MI is active it hides the claim button and instance-level flow actions.
- The AI authoring package bundles every guide under `docs/` except `docs/enhancements/`, `docs/plans/`, and `docs/refactoring/`, and generates `workflow.schema.json` from the model.
- Both browser suites start their published hosts with `--environment Development`.

Use existing staging/leases/result persistence, instance/gateway/token locking, variable version fences, cancellation helpers, grouped projections, and browser fixtures. The parent records [later-release exclusions](task-05-multi-instance-automatic-activities.md#later-releases). The proposed defaults and original open questions are resolved by the contracts below; implementation discoveries must update the owning plan before diverging.

## Plan

### 1. Scope and invariants

Support collection bodies on `serviceTask`, `scriptTask`, and `callActivity`. Keep the existing `userTask` path and its defaults, assignee normalization, one-per-actor behavior, outcome voting, result envelope, and authorization unchanged. Preserve ordinary single-call behavior as well.

An automatic body owns one execution token and one multi-instance parent execution. Items are persisted work owned by that parent, not sibling graph tokens or user tasks in the parent instance. A call item additionally owns a child workflow instance, which has its own ordinary tokens/tasks. Each body has exactly one ordinary unconditional outgoing flow, using today's service/script/call topology; it has no selectable outcomes, engine-only default, completion priorities, or `CountFlow`/`PercentFlow` voting.

Require `asyncBefore: true` on a service/script MI node. The editor enables it when the user enables MI; imported definitions without it are rejected, not silently made synchronous. The existing parent entry job initializes the body instead of invoking the activity once. `asyncAfter` remains optional for service/script and applies once after aggregate publication, never once per item. Calls remain intrinsically durable: **do not permit `asyncBefore`, `asyncAfter`, or authored `job` settings on MI calls**. They use a dedicated body-initialization job and existing call message processing. The normal durable publication/readiness gate must recognize all three activity types.

Error, timer, and conditional boundaries may attach to the body in this release. Error boundaries remain interrupting: calls retain one catch-all with no HTTP matcher, scripts retain their catch-all, and REST tasks retain their status-matching rules. Timer/conditional boundaries support both `cancelActivity` modes using the existing event contracts and the lifecycle in §7. Extend server/editor host eligibility explicitly for automatic MI calls; ordinary non-MI call restrictions stay unchanged. Boundaries attach to the parent activity activation, never an individual item.

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

This fragment belongs in a service/script/call node's `multiInstance` property. `items` must be a declared instance variable or readable shared alias with `isArray: true`. `itemResults` must be a different declared, writable instance variable or `readWrite` shared alias with scalar `dataType: "json"` and `isArray: false`. An instance target has a JSON array default such as `[]`; a shared target uses its existing catalog contract, never an authored shared default, and must accept the initialization value `[]`.

| Property | Automatic-body contract |
| --- | --- |
| `mode` | `parallel` or `sequential`; omission keeps the existing `parallel` default. Canonicalize recognized casing; reject null/unknown values. |
| `source` | `collection` only; omission keeps the existing default. Reject cardinality and `cardinalityExpression`. |
| `collectionVariable` | Required declared instance array or readable shared array binding; support the existing declared element types, including `json[]` for objects. Copy the collection once at successful body initialization under the applicable locks; later shared changes do not alter its items or count. A scalar `json` holding an array is not a collection source in this release. Preserve order, duplicate values, casing, and whitespace; these are not usernames. |
| `completionEvaluation` | Required as `afterAll`: automatic bodies always wait for all successful items. The model and the editor loader both turn an omitted value into `afterEach`, so validation rejects an omitted value and `afterEach` alike on an automatic body, with a message naming `afterAll`. The editor writes `afterAll` when MI is enabled on a service, script, or call node. Human MI keeps its `afterEach` default. |
| `onePerActor` | Only omitted/false is accepted; it has no automatic-work meaning. |
| `maxConcurrency` | Optional integer 1–1,000. In parallel mode, an omitted value uses the `Workflow.MultiInstance.DefaultConcurrency` engine setting. The effective limit is the smallest of that value, the `Workflow.MultiInstance.MaxConcurrency` cap, and the item count; progress reports it. Sequential accepts omission or 1. Reject wrong JSON types, fractional numbers, zero, and negatives. |
| `outputVariables` | Required list, possibly empty for side-effect-only activities; at most 100 declarations. Names are bounded to 300 Unicode scalars, unique case-insensitively, and cannot use reserved prefixes or collide with any declared/discovered process producer name. |
| Local declaration fields | `name`, `dataType`, `isArray` (default false), `nullable` (default false), optional `defaultValue` and `validation`. Apply existing type/default/validation semantics, but no shared binding, form fields, or variable IDs. Reject unrecognized declaration fields. |
| `resultVariable` | Required writable instance or `readWrite` shared scalar JSON target accepting an array. It cannot overlap the collection source, a local name, an item output/status/assignment target, a boundary error target, or another MI node's aggregate target in the definition. Compare aliases case-insensitively and resolved shared keys by their catalog identity so alternate spellings cannot bypass the rule. Shared aggregates use value-version fencing, not a global one-body-per-key lock. |

At body entry, resolve the source against its declared contract. Missing, null, non-array, or invalid elements are entry failures. Validate each element with the existing declared element-type rules; a source's whole-variable `nullable` flag never makes a null collection valid. Stop enumeration at the configured instance limit plus one; do not materialize an unbounded source before checking it. An empty array is valid for automatic work and schedules aggregate completion without any item jobs. Human MI rejects zero items, so this is new behavior: an empty body creates no item or node-activity rows, and §7 defines the evidence it leaves.

### 3. Input snapshots, local outputs, and aggregate publication

Persist one immutable parent snapshot, referenced from the body row, containing the frozen collection (instance or shared) and the instance inputs that item work may read. For REST templates, NCalc expressions, and call input mappings, these are the variables they reference, as ordinary durable REST and NCalc jobs filter today. JavaScript can read any name, so it gets every non-shared instance variable, as an ordinary durable JavaScript job snapshots its whole context. The collection is stored only here: item rows hold the index, and `mi.item`, item detail, and result rows read the element from this snapshot. Service/script work retains its causal actor/allowlisted claims. Calls use their fixed system actor, without inherited roles, claims, or delegation. Define the snapshot instant as successful durable body initialization, after the service/script `asyncBefore` wait or the intrinsic call-body entry wait, not the earlier HTTP request. Record shared source identity/value version as well as snapshot time. Missing/unset or invalid shared collections fail entry through the existing catalog/value rules; never silently turn them into an empty list.

Each item receives an independent local buffer and the same read-only context contract:

| Context key | Value |
| --- | --- |
| `mi.item` | The exact copied collection element, preserving its declared type and JSON value. |
| `mi.index` | The zero-based item index, from 0 through collection length minus one. |
| `mi.total` | The frozen collection length. |

These keys are reserved context, never writable locals or persisted process variables. Instance inputs and the item are frozen for the whole body. Other shared reads are captured under the exact shared access plan when each REST/script item first stages; MI calls capture them at item admission under the parent/shared locks before crossing into child-start processing. Persist this item snapshot for all execution and delivery retries. The collection alias, including any reference resolving to the same shared key, always resolves to the body's frozen collection. Permitted `config.*`/`setting.*` values and `sys.now`/`sys.today` are read when each REST/script item first stages and kept for its retries. A credential or shared value changed before a later item stages can therefore reach that item; sequential items may observe earlier committed shared writes. Authors needing the same non-collection value for every item copy it into an instance variable before the body. Calls retain whole-variable mappings without expressions or configuration sources, extended to shared aliases in [TASK-05D](task-05d-multi-instance-call-activities.md). REST/script jobs preserve existing attempt-specific `sys.jobId`/`sys.jobAttempt` behavior; `sys.jobId` remains stable across retries of that item. Capture `FlowInfo`, where already allowed for scripts, once for the body. Do not allow progress counters such as `mi.completed`/`mi.remaining` to introduce completion-order-dependent item calculations.

Item local/shared-output rules apply only inside an automatic MI execution:

- Service `outputMappings` and `statusVariable` target declared locals or declared `readWrite` shared aliases. Apply the same path extraction, defaults, type checks, validation order, and all-or-nothing successful mapping batch as ordinary REST work. A status target must be scalar number and cannot overlap another mapping by alias or resolved key.
- NCalc assignment targets and JavaScript `execution.setVariable` target declared locals or `readWrite` shared aliases. Ordered assignments can read previous staged writes in the item's private buffer. Getters return copied JSON values; mutating an input cannot change its persisted snapshot or the live catalog.
- Call output mappings target these same locals or writable parent shared aliases; input mappings and invocation delivery follow [TASK-05D](task-05d-multi-instance-call-activities.md). A called child is a separate workflow and writes its own variables normally; only explicitly mapped results return to the parent item. Do not carry parent shared-catalog locks into child-start processing; never lock two existing instances together.
- A local default is applied to a fresh buffer on every attempt. A required non-nullable output may remain unset while execution is in progress, but all declared outputs must satisfy their final contracts before successful completion; nullable unset outputs become JSON null. An empty declaration list produces `{}`.
- Reject direct item writes to ordinary parent instance variables, read-only shared aliases, item context, undeclared names, and the reserved source/aggregate/error targets. No local name may shadow a process input, shared binding, or process producer discovered on another node/flow. A dynamically constructed JavaScript target is checked at runtime, not assumed safe after static validation.
- Permit shared dependencies in REST URL/header/body placeholders, mappings, defaults, validations, and assignments, including aggregate/error-target validation dependencies. Resolve catalog bindings and access before evaluation, include them in exact access plans, and enforce binding type/nullability/validation/lifecycle contracts. Guard runtime templates before substitution: an unavailable or unauthorized shared alias must not silently become JSON null. Dynamic JavaScript reads/writes follow the existing conservative per-node plan and runtime authorization; do not lock unrelated definition bindings.
- An execution or mapping failure discards the entire candidate local/shared write batch. A service failure may retain status in diagnostics, but does not commit a failed attempt's shared status. Only the selected error boundary may write its declared instance or `readWrite` shared error target through separately fenced routing. A publication conflict preserves the successful candidate instead of treating it as a failed execution attempt.

Commit an item's validated shared write set, local result, write correlations, success/count/ledger update, and refill/completion signal in one transaction. Use the normal shared-variable store and audit path: shared target writes are not implicitly copied into instance variables or ordinary instance history, and the aggregate `variables` object contains declared locals only. Persist bounded candidate locals/shared values and target identities/value versions independently of job retention before publication, so an accepted HTTP response, script result, or child result can be recovered without re-execution. Apply the existing 1 MiB staged payload limit to the combined item input/candidate data; do not hide unbounded shared payloads outside snapshot/result accounting.

Capture shared output `ValueRevision` fences at first item staging/admission, then lock and revalidate catalog access, contracts, and the complete write batch before publication. Concurrent items or other instances writing the same key may conflict; parallel mode is not an ordered reduction. An output conflict opens an operational incident with no partial writes or success increment. Authorized retry publishes the saved candidate with explicitly refreshed output fences, as an acceptance of that candidate against current state, without refreshing its inputs or recalculating its read-modify-write values. Such a retry can replace a newer value; it is not an automatic merge or increment operation. Show this consequence in recovery help and test it. A binding/contract/lifecycle failure remains blocked until actually resolved and is never bypassed by a fence refresh.

Implement a dedicated automatic-item/aggregate publication retry branch. Today's generic `WorkflowJobRepository.RetryIncidentAsync` clears saved results and clears the snapshot for `output_version_conflict`; reusing it unchanged would repeat external work. Keep ordinary-job behavior unchanged, but retain automatic candidates and their frozen inputs, requeue only publication, and preserve item/activation/lease fences. Duplicate finalization cannot append a second shared audit write. Completed siblings' shared writes remain committed after later failure or cancellation; this release provides no compensation.

Candidate persistence must survive a failed publication transaction: commit the candidate before attempting publication, or commit it together with the publication incident without applying output writes. Throwing a conflict must not roll back the only saved HTTP/script/child result. Every candidate save and later publication rechecks the owning activation, item, lease and cancellation fences; saving a late result cannot revive cancelled work.

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

Reset the instance or shared aggregate target to `[]` once at successful body initialization, using the normal variable store. Validate the reset before admitting any work; capture the resulting instance value version or shared `ValueRevision`. Intermediate local successes stay on item rows; explicit item shared writes commit separately under the item contract and never append to this aggregate. After every item succeeds, publish the complete array and the parent continuation atomically, subject to target validation and its version fence. No item can publish a partial aggregate; only the error-boundary path below publishes one, for the whole body. Two tokens can still reach one node at the same time, for example through an exclusive merge, so at most one automatic body per node may be open in an instance, enforced by a partial unique index. A second token's entry job that finds the node's body open changes no body data: it records why it waits and reschedules with the dispatch backoff, without consuming attempts or taking an error boundary. Its own conditional boundary may still interrupt that entry. Bodies of one node therefore run one after another, and each publishes its complete result. This index does not serialize a shared aggregate key across instances or definitions: another reset or write may cause a publication incident. Resolve that conflict through explicit publication recovery, never silently overwrite or reserve a shared key for the body's lifetime.

Instance aggregate reset and publication can trigger conditional work on the body or elsewhere in the workflow; shared writes do not create conditional notifications because those conditions remain instance-only. Install the parent/items and host timers before draining reset notifications so cancellation can find them. Drain notifications and recheck activation/execution state before admitting children or taking the normal continuation. If a notification interrupts the body, commit the accepted write and cancellation without enqueueing fresh item work or taking a second route. No boundary observes uncommitted item-local buffers.

When an error boundary catches an item failure, stop the body before publishing anything. One transaction records the winning failure, interrupts the body as §7 describes, and queues the body's completion job in the reserved slot, replacing any queued dispatch job; it writes no aggregate and routes nothing. The completion job then publishes a partial result and routes the boundary, with the same validation, version fence, size limit, and notification draining as final publication. The partial result holds one row per item in index order: completed items keep their local `variables`; the winning failure and any already failed siblings are `failed`; unfinished and never-started items are `cancelled`. Failed and cancelled rows carry `"variables": {}` and no diagnostic; the boundary's error variable, when declared, carries the winning diagnostic. A version-fence conflict opens an incident on that completion job. The siblings were already stopped, so nothing keeps running while it waits; its retry refreshes the fence and repeats publication and routing from saved state without rerunning work. If the partial result fails the target's validation or exceeds the size limit, the boundary continues without it and its diagnostic says so.

On instance/scope cancellation or timer/conditional interruption, or while an incident is unresolved, initiate no aggregate write: the target normally remains `[]`, and nothing restores that entry value over a later authorized write. A reset/publication already accepted in the transition that triggers a conditional boundary remains committed; suppress the ordinary continuation. Retain completed, failed, and cancelled item evidence for authorized detail queries. Prior committed shared item writes are not reverted. A concurrent administrative/branch/other-instance edit to either aggregate scope must cause a publication conflict, not silent overwrite.

Use a distinct durable completion job over saved item results; it runs both the success path and a caught failure's error-boundary path. An instance/shared output-version conflict opens an incident on that job; automatic publication-only retry refreshes the output fence under authorized recovery but cannot clear item results or invoke work again. Recheck live catalog/access/validation dependencies under the exact shared locks on every publication attempt. Catalog lifecycle, contract drift, and version conflicts remain operational incidents; do not disguise them as business errors. An assembled array rejected by its valid target contract or exceeding the size limit takes the whole-body error boundary when attached, publishing nothing, and otherwise faults the instance through its normal terminal-status path; do not repeatedly retry the unchanged aggregate automatically. Give these failures and entry failures stable, value-free codes for incidents, a boundary's error variable, and the instance `fault` (proposed: `multi_instance_source_invalid`, `multi_instance_result_invalid`, and `multi_instance_result_too_large`), plus an `ExecutionTokenTerminationReasons` value beside `callFailed`. The body has no parent visit, so an aggregate fault leaves no node-activity row to fault. Failure of downstream continuation rolls back publication/continuation together and retries this completion phase only. `asyncAfter`, when authored, records the normal outgoing traversal only when that continuation executes, after host subscriptions have closed.

### 4. Limits and admission

Keep `Workflow.MultiInstance.MaxInstances` (default 1,000) as the total item limit. Add two dynamic engine settings with an effective range of 1–1,000: `Workflow.MultiInstance.DefaultConcurrency` (default 4) applies when a parallel body omits `maxConcurrency`, and `Workflow.MultiInstance.MaxConcurrency` (default 1,000) caps both authored and default values, so operators can lower it to protect downstream systems. Settings management stores engine values as unvalidated strings, so the consumer parses each value as a positive signed 32-bit integer after trimming whitespace and a leading `+`. It caps a parseable value above 1,000 at 1,000 and uses the default for a missing, blank, malformed, overflowing, zero, or negative value, as `WorkflowBatchActions.MaxAffectedTasks` does. A default above the cap yields the cap. Seed both in the 05A migration. The cap's name mirrors the authored `maxConcurrency` it caps; keep it distinct from the Worker's own `MaxConcurrency` option in documentation. Snapshot the total-item limit and the effective concurrency when the body initializes, and report the effective value in progress. Later setting changes affect new bodies; they do not cancel or expand existing admitted work.

Use persisted slots under the parent lock. Pending rows are cheap work descriptors, not queued jobs or call reservations. Admit only the next ascending-index window. A retrying, incident-blocked, or waiting call item retains its slot; successful result delivery releases one and admits the next item transactionally. When an item-level start/execution/output incident blocks the body, pause new admission while allowing already admitted work to settle. A job incident inside a called child simply occupies that item's slot; it does not pause otherwise available slots in its parent body. Sequential mode never starts the next item before the previous item commits success. When call admission refuses an item because its root already has `WorkflowCallActivities:MaxOpenInvocationsPerRoot` open calls, the item stays pending as back-pressure rather than failing; [TASK-05D](task-05d-multi-instance-call-activities.md) defines that rule. Progress reports whether admission is blocked and why: an item incident, queue capacity, or call capacity.

Shared-input capture can fail before a call item has an invocation. In that case the owning entry/dispatch control job carries the item-correlated admission incident; the item stays pending with a blocked reason and no admitted slot, invocation or child. Pause new admission until recovery. Retry admission only and freeze shared inputs on its first successful admission; do not attempt same-invocation recovery when no invocation exists. Missing/archived/changed bindings or access failures are operational incidents, while a value failing an otherwise valid input contract follows the adapter's error-boundary/incident policy. This is distinct from ordinary queue/root-capacity back-pressure.

The MI window supplements the per-instance open-job limit (`WorkflowJobCapacity`, checked by `EnqueueInstanceJobAsync`) and the Worker's `MaxPerInstance`, `ActivityConcurrency`, and `MaxConcurrency`; it does not override them. Current Worker defaults are 4, 6, and 8 respectively. No process-local semaphore may be the authority across replicas. Each Worker leases at most `MaxPerInstance` jobs of one instance, so a REST/script window wider than that times the Worker count runs no more items at once; document this beside the settings.

The per-instance limit is twice `Workflow.MultiInstance.MaxInstances` (2,000 by default), counted from open job rows by `CountOpenByInstanceAsync`, and a finishing job already frees its own slot for one replacement, so REST/script refill is one-for-one. The reservation below still matters for the initial window and for call results, because a `callResume` job belongs to the child instance and frees nothing in the parent. Implement the reservation by counting each unsettled automatic body once in `CountOpenByInstanceAsync`, which both the single and batch enqueue paths use; the body's own dispatch and completion jobs occupy that reservation and are not counted again. A reservation the count cannot see protects nothing, and a parked placeholder job would appear in Operations as real work.

Extend capacity accounting to reserve one control-job slot per open body, separate from its admitted item jobs. Transfer the entry job's slot into that reservation when initializing; do not admit a new body without the reservation. Persist child success before attempting refill. When capacity prevents refill, enqueue a deduplicated `multiInstanceDispatch` control job in the reserved slot; reschedule it with bounded backoff without consuming activity attempts or opening an incident for ordinary capacity contention. Admission inside a job must test capacity without throwing, because today's `job_capacity_exceeded` path would roll back the successful item result and consume attempts. Contention can still stall a body: `MaxInstances` can be set as low as 1, making the instance limit 2, and reservations alone can then fill it. When dispatch finds the body with no admitted, unsettled item and its backoff is at the maximum delay, nothing in the body will free a slot, so it opens a `job_capacity_exceeded` incident instead of waiting silently; retrying the incident repeats admission. Dispatch reads pending rows and admits only available capacity. The same reservation transfers to aggregate completion when all items succeed, then releases on body settlement. Define the reservation and control-job generation fences in SQL so concurrent bodies, cancellation, and restarts cannot leak reservations or leave pending work without a durable wake-up. Capacity recovery must never invoke an already successful child again.

Introduce shared automatic-MI constraints with an initial 1 MiB serialized parent input snapshot limit and 1 MiB complete result-array limit, including item values and envelope overhead. Apply existing REST response, Jint value/memory, and 1 MiB per-call input/output snapshot limits as well. Every result row repeats its item, so at initialization compute the bytes the rows need whatever their outputs, the copied items plus envelope overhead, and fail the entry before any item runs when that alone exceeds the result limit. Track accumulated result bytes while settling items and enforce the final encoded limit before publication; an over-limit result is a failure retrying cannot change (§3), never truncated data. Bound local declarations to 100 and fan-out insert batches independently of HTTP payload limits. Document these limits before implementation tests pin them.

Concurrency is a limit on admitted logical work and valid leases. After a network partition or lost lease, an old remote request may still be running while its replacement retries. Do not promise a physical remote-call maximum or exactly-once HTTP effects. Preserve stable per-item job IDs and document the authored idempotency header requirement for downstream systems.

### 5. Persistence and migration

Add an additive migration; all proposed database objects belong to the fixed `flowbit` schema.

| Storage | Proposed changes and constraints |
| --- | --- |
| `multi_instance_executions` | Add activity kind, definition/activation correlation, immutable input snapshot reference, aggregate scope/key/value fence, captured effective concurrency, admission/completion state and admission-blocked reason, failed count and bounded result-size accounting. A partial unique index allows one unsettled automatic body per `(InstanceId, NodeId)`, not per shared key across instances. Call bodies also store their pinned child definition/start and pin lifecycle. Default existing rows to human/user-task kind; retain their existing fields and meaning. |
| `multi_instance_activity_items` (new) | Stable ID; instance/execution ownership; immutable zero-based index (the element stays in the parent snapshot); pending/active/completed/failed/cancelled state; durable execution-versus-publication phase, bounded candidate locals/shared writes, captured output value versions, committed local result/value-free shared write correlations, diagnostic and timestamps. Jobs and ledger rows reference items, never the reverse, so pruning them cannot break an item. Unique `(MultiInstanceExecutionId, ItemIndex)`; nonnegative index; ownership FKs; indexes for parent/status/index admission and paging. |
| `workflow_jobs` | Nullable automatic-item correlation for parent-owned REST/script work; new `multiInstanceStart` (intrinsic call-body entry), `multiInstanceItem` (REST/script), `multiInstanceDispatch`, and `multiInstanceComplete` kinds. Service/script entry reuses `asyncBefore`. Existing call messages use invocation correlation, not a new parent-item FK on child-owned rows. Add unique generation/completion fences and at most one open dispatch/completion control job per reserved slot. Update ownership constraints for each job kind; §6 lists each kind's queue class and retry policy. |
| `call_activity_executions` and call results | Nullable parent-owned automatic-item FK with unique non-null ownership; filtered ordinary-call activation uniqueness; separate item/body fences. Preserve child-owned result/message storage and cross-instance lock boundaries. Keep ordinary invocations unchanged and retain settled invocation identity through history pruning. |
| `workflow_job_snapshots` or dedicated parent snapshot | Store the shared immutable batch context once and reference it from the body row, so it outlives the entry job; item jobs may name it too. A `workflow_job_snapshots` row is already capped at 1 MiB by `CK_workflow_job_snapshots_size`. Retain normal per-item staged invocation/result state and size checks. Avoid copying the entire collection into every child snapshot. |
| `execution_tokens` | Explicit automatic-body wait correlation distinct from single-job waiting. `WaitingJobId` owns entry/completion/async-after jobs only, never all item jobs. Widen `CK_execution_tokens_wait_shape`, which requires a waiting job or timer subscription for every wait state. |
| Timer/conditional subscriptions and occurrences | Extend host eligibility and host-active predicates to automatic entry/body/completion waits. Reuse token activation and occurrence identities; associate the admitted body where needed without requiring one for queued-entry conditionals. Preserve unique firing/delivery fences, non-interrupting latched delivery, cancellation and retention; never generate a subscription per item. |
| `node_executions` | Add `automaticActivityItem` kind and nullable item FK; update existing check constraints/indexes. `CK_node_executions_multi_instance_shape` requires `UserTaskId` on MI rows, so the new kind needs its own clause. One child ledger row per item, including pending rows, and no duplicate parent visit; the parent is represented by its execution/progress row. Moving a token to an MI node already skips the parent visit, because the token snapshot's `IsMultiInstance` does not check the node type. |

Final SQL names and EF mappings must be kept consistent across entities, repository records, migration SQL/designer/snapshot, retention, DTOs, and schema documentation. Add one-active-body-per-token/activation and job/item/invocation ownership constraints so retries cannot create a second batch, swap work between items, or start a second child for one item. Entry failures before item creation remain visible through the parent entry job/incident without inventing a child visit. Include pinned-target deletion/unpublication guards even before an invocation has a child ID. `node_executions` and `workflow_jobs` are large: add their widened check constraints `NOT VALID` and validate them in a separate migration that runs outside a transaction, as `AddCronTimerSchedules` and `ValidateCronTimerScheduleConstraints` do.

Build the audit list from the table, its entity, and `MultiInstanceExecutionId`, not the table name alone: on 2026-11-11 that search found more than fifty non-migration source files, all assuming human work. Give each an explicit human-kind filter or an automatic branch, pinned by a regression test; files that already require a `userTask` node, such as the MI flow and interrupt paths in `WorkflowEngineService.cs`, need only the test. The audit covers at least:

- `AdministrativeActionCandidateRepository`, whose candidate query lists every active row as an administrative position, plus `AdministrativeActionBatchService`, `AdministrativeActionBatchJobProcessor`, `AdministrativeActionBatchRepository`, and `WorkflowEngineService.AdministrativeActions.cs`.
- `WorkflowRuntimeRepository` and `WorkflowRuntimeRepository.RolePolicies.cs` (inbox, progress, interrupts, and role management), `UserTaskRoleManagementService`, and `WorkflowEngineService.Reactivation.cs`.
- Projections and queries: `WorkflowInstanceProjectionService`, `WorkflowInstanceQueryService`, `NodeExecutionQueryRepository`, `NodeExecutionQueryService`, `NodeExecutionEndpoints`, `InstanceDiagramHistoryQueryRepository`, and the position mapping in `InstanceVersionChangeBatchService` and `InstanceVariableUpdateBatchService`.
- `WorkflowEngineService.VersionChange.cs`, `WorkflowVersionCompatibilityEvaluator`, and `WorkflowVersionCompatibilityModels.cs`.
- `WorkflowJobRepository`, and the user-task async-after continuation, which labels any job carrying `MultiInstanceExecutionId` as `multiInstanceAsyncAfter`.
- The mappings in `AppDbContext` and every entity that references an MI execution: `ExecutionTokenEntity`, `WorkflowInstanceEntity`, `WorkflowJobEntity`, `NodeExecutionEntity`, `UserTaskEntity`, `MultiInstanceFlowCountEntity`, `AdministrativeActionBatchItemEntity`, `SequenceFlowOccurrenceEntity`, and `InstanceHistoryEntity`, plus their service records and shared DTOs.
- UI: `InstanceDetail.razor`, which calls the human interrupt-flows route for a sole active execution and hides the claim button and instance-level flow actions while any MI is active, plus `InstanceMultiInstanceResults.razor`, `InstanceDiagram.razor`, `InstanceDiagramOverlay`, `ExecutionPositionText`, `NodeActivity.razor`, `NodeExecutionDetail.razor`, `TaskManagement.razor`, `AdministrativeActions.razor`, `InstanceAdministrativeActions.razor`, `AdministrativeBatchFormatting`, `InstanceHistory.razor`, and `WorkflowApiClient`.

Create item rows and ledger rows with set-based operations. Settle/cancel unfinished items in bounded SQL batches. Progress and counts must use grouped queries rather than loading every item JSON payload. Do not create `user_tasks`, human role policies, or human vote-count rows for automatic parent items; called workflows retain their normal tasks/policies.

Retention must protect pending/active/incident-blocked bodies and all required items, jobs, snapshots, invocations, call results, and completion records. Existing protection through `token.WaitingJobId` alone is insufficient: completed jobs are pruned by age while their instance runs, so a body that outlives the completed-job period (30 days by default; an MI call waiting on human work in its children easily does) would lose its entry job, its completed item jobs, and any snapshot reachable only through them. Extend the `workflow_job_snapshots` protection in `RetentionRepository` to keep a snapshot that an unsettled body references, and keep each item's local result on its item row, so pruning a completed item job loses nothing. Item rows are runtime state, like `multi_instance_executions` and `user_tasks`, and are never deleted while their instance exists; their ledger rows follow node-activity retention, which may delete ledger rows but never items. Specify FK deletion order and protection after a job or child succeeds but its parent still waits. Preserve completed item/call results until parent publication is durable, and keep call identity/fence records according to existing call retention rules. Retain a compact automatic-item identity for the invocation's lifetime; pruning payload/history must never null its item FK and make it appear to be an ordinary call under the filtered uniqueness rule. Historical pruning follows existing settings and marks `HistoryPrunedAt` where required; the migration does not fabricate historical automatic executions.

### 6. Engine lifecycle and transaction protocol

Implement a focused automatic-MI coordinator, with repository ports, rather than adding automatic branching throughout human task completion. A dedicated `WorkflowEngineService.AutomaticMultiInstance.cs` partial is a proposed implementation location. Factor only shared mechanical helpers from existing human MI and job code. REST/script stage/invoke steps below do not replace the invocation/message lifecycle in [TASK-05D](task-05d-multi-instance-call-activities.md).

| Phase | Transactional responsibility |
| --- | --- |
| Enter | Install/evaluate host conditional subscriptions for the new token activation, then queue parent `asyncBefore` for REST/script or intrinsic `multiInstanceStart` for calls if still eligible. Commit its wait/activation once. No item activity or child workflow starts in the caller's transaction; do not start host timer clocks yet. |
| Initialize | Lock and fence entry job/token; if another body of this node is open, reschedule without consuming attempts (§3), preserving this token's conditional subscriptions; validate/snapshot inputs and shared collection; install parent/items/ledger and transfer the control reservation; arm timers once, reset aggregate, drain instance notifications and recheck fences; admit the first window or queue dispatch; replace entry wait with body wait; complete entry job. Duplicate execution observes the existing body/timers. Empty input queues completion directly. |
| Stage item | Fence parent activation, item ownership, stable job ID and lease generation; lock exact shared dependencies after runtime rows, build/persist the item context and output version fences from body inputs plus current shared/config values and fresh locals. Commit and discard tracked state before external work. Calls capture shared inputs at admission under the receiving-parent transaction. |
| Invoke/evaluate | Use the existing HTTP invoker or sandboxed script evaluator outside the long database transaction, preserving timeouts, cancellation, redaction, and attempt metadata. |
| Start/deliver call item | Use its parent-owned invocation and existing call messages; create one child from the body pin, then deliver saved child outputs into local item storage under the receiver's lock. Waiting children consume slots, never long-held leases. |
| Finalize item | Reacquire current runtime/shared state in normal lock order; reject stale leases/activations or a closed parent; persist the candidate before publication, then atomically validate/fence/commit its whole shared write batch and local result, update counts/ledger, and signal refill/completion. A publication conflict retains the candidate without marking success; capacity contention cannot roll back accepted success into reinvocation. Refill needing new shared keys runs through reserved dispatch rather than descending lock order. |
| Complete body | Fence the unique completion job, reconstruct ordered saved results, validate instance/shared output version/contract, publish one aggregate, drain instance notifications and recheck fences, then close host subscriptions and settle/continue or queue its single async-after job if still eligible. After a caught error, host subscriptions are already closed; this job publishes the partial result and routes the error boundary instead. Timer/conditional interruption follows §7 without this partial-result phase. |

Each job kind has a fixed queue class and retry policy. `BuildJobCreate` otherwise queues every job of a service or script node in the activity class; dispatch and completion are control work and must not wait behind item jobs for activity slots.

| Job | Nodes | Queue class | Attempts and delays | Automatic-activation guard |
| --- | --- | --- | --- | --- |
| `asyncBefore` (body entry) | Service, script | Activity, as today | Node `job` policy; entry failures follow its `failureHandling`. Waiting for an open body of the same node reschedules without consuming attempts. | Counts the body once. |
| `multiInstanceStart` | Call | Control | Default delays (10 s, 1 min, 5 min); calls reject an authored `job`. Waiting for an open body of the same node reschedules without consuming attempts. | Counts the body once. |
| `multiInstanceItem` | Service, script | Activity | Node `job` policy for each item: `retryDelays` plus one attempts, and its `failureHandling`. | Not counted. |
| `callStart`, `callResume`, `callCancel` | Call items | As today | Existing call behavior. | Item starts are not counted. |
| `multiInstanceDispatch` | All three | Control | Capacity backoff never consumes activity attempts; the nothing-in-flight/max-delay rule opens a capacity incident (§4). Also owns correlated pre-invocation shared-input admission failures under §4/TASK-05D; these are not capacity retries or item re-execution. | Not counted. |
| `multiInstanceComplete` | All three | Control | Default delays; runs the success path and a caught failure's boundary path. A version conflict opens an incident and other failures follow §3. | Not counted. |
| `asyncAfter` | Service, script | Activity, as today | Node `job` policy. | As today. |

Extend normal mutation order as **instance → active gateway execution/state/branch → parent token → MI execution → automatic items in ID order**, then acquire job/incident locks according to the existing durable protocol. Call-body pinning and every item call-start attempt acquire the target-family lock before this runtime sequence; call messages obey the one-existing-instance rule. Where a transition also touches human work, preserve its established group ordering. Audit leasing, heartbeat, retry, cancellation, and retention for inverse locks; job leasing must remain a short independent transaction before runtime mutation. Never hold instance/item/shared row locks while calling HTTP or evaluating a script.

Acquire shared catalog/current rows only after the applicable runtime rows, by exact key in ordinal order, under an ambient transaction. Extend `SharedVariableAccessPlan` and the monotonic segment proof for body initialization/reset, item staging/publication, admission, aggregate/error publication, and timer/conditional continuations. Include shared validation dependencies, source/target keys and dynamic JavaScript's conservative per-node bindings; never prelock every workflow binding. Compute exact per-phase key sets before mutation and include their acquisition sequence in the segment proof, rather than prelocking future nodes. Preserve rejection of descending synchronous acquisition. Use reserved dispatch for initial admission or refill that would acquire an earlier key, and treat durable completion/conditional delivery as separate proved segments. No shared lock spans HTTP, script evaluation, waiting children, or a body lifetime. Call child creation consumes saved inputs without parent shared-catalog locks; it still acquires family then parent runtime/item/invocation fences before creating its new child, preserving the one-existing-instance rule. Delivery writes parent shared targets under the receiving parent's transaction only.

Item fences include immutable definition, parent activation, MI execution, item, job/invocation identity, lease token/generation where applicable, and eligible item/parent state. A parent token ID alone is insufficient. Keep ordinary `RequireFencedToken` behavior for non-item jobs and add specific item/invocation fences to `JobStillOwnsItsWorkAsync`, call-start/result-delivery, finalization, and conflict handling.

Every finalization refreshes database state; do not reuse EF entities left from staging. Under the instance/parent lock, the first accepted transition wins. Duplicate success, duplicate leasing, simultaneous last-item completions, and retry requests after cancellation cannot increment counts twice or enqueue duplicate aggregate jobs.

### 7. Failure, cancellation, audit, and recovery

Timer/conditional boundary ownership is the parent token activation, with at most one subscription per boundary/activation and the existing occurrence fences. Item creation, retry, refill and output-conflict recovery neither re-arm subscriptions nor reset their clocks. Keep the limit of eight timer and conditional boundaries combined per host, separate from its error-boundary allowance. Support all timer schedules already valid on boundaries, including recurrence and business calendars where applicable, without adding start-only cron settings. Preserve conditional `atomic`/`durableAsync` behavior, initial evaluation, and non-interrupting false-to-true rearming. Conditional predicates read current declared persisted instance variables only, not `mi.*`, item-local buffers or shared aliases; shared writes do not wake them. This is the current conditional-event contract, not a deferral of automatic-MI boundary support.

Conditional subscriptions attach and evaluate at token entry, including a second token waiting for the node's existing body. An initially true interrupting condition can cancel that entry before any body, reset, items or child starts exist; it must not cancel the other token's body. Host timers arm once at successful body initialization, before reset/admission, using one persisted activation instant. They do not count time queued for a Worker or another body's settlement. Both kinds remain eligible during item execution, capacity waits, recoverable incidents and pending normal completion. On normal publication, drain instance notifications and recheck fences before closing host subscriptions and advancing or scheduling `asyncAfter`; no host subscription follows that async-after wait. Empty bodies follow the same ordering.

An interrupting timer/conditional boundary atomically wins against item/completion/error transitions under the host locks. It stops admission, cancels unfinished items and their jobs/incidents, abandons open invocations, releases the body reservation, closes competing subscriptions and routes the host once. Before initialization it cancels only the entry activation. It initiates no aggregate or partial-result write and does not wait for aggregate publication recovery. Already accepted writes, including the aggregate that triggered an instance condition, remain committed and the normal outgoing route is suppressed. Completed siblings and their shared effects stay completed. Call descendants are fenced immediately and cancelled durably under TASK-05D; late HTTP/script/child results cannot publish after interruption.

A non-interrupting boundary creates a sibling graph token for each accepted occurrence and keeps the parent body, slots and items running. Recurring timers and conditional false-to-true transitions retain their existing deduplication, capacity/backoff and loop protections. A created sibling or already accepted non-interrupting durable occurrence survives normal host completion according to existing scope/instance cancellation rules; closing the host prevents new captures rather than losing accepted work. Include these additional boundary tokens in execution positions and flow history, distinct from automatic MI items. A boundary path reaching this MI node again waits under the same one-open-body rule.

Error-boundary capture closes host timer/conditional subscriptions before the partial aggregate or error variable is written, so a pending normal host event cannot steal the selected error route while completion waits in an incident. Accepted non-interrupting occurrences keep their normal independent delivery semantics. Persist the winning terminal transition and reject stale event, job, retry and child-result delivery across restarts.

For REST/script, reuse existing failure classification and exact/range/catch-all error-boundary matching. `boundaryFirst` tries an applicable boundary before retrying; `retryFirst` exhausts its configured attempts first. With no matching boundary, apply the ordinary retry schedule and then open an incident. Apply this consistently to local-output validation failures and item execution failures; pre-fan-out input failures belong to the entry job and use the same applicable policy. Calls follow the [TASK-05D call failure contract](task-05d-multi-instance-call-activities.md); do not turn a terminal child fault into an endlessly retryable item.

When an item failure is caught, atomically record the winning failure, fault the automatic parent-item ledger visit, interrupt the parent, cancel unfinished item jobs/rows, stop admission, and queue the completion job. That job then publishes the partial result described in §3, writes the selected boundary diagnostic through normal variable mutation tracking, and routes the parent down the boundary once. Previously completed items and called workflows remain completed; an output-delivery failure must not change the completed child's status or ledger. An already failed sibling retains its failure evidence while its open recovery path is closed as superseded. Cancelled pending items remain unstarted.

For exhausted REST/script work or a recoverable call start/output failure without a boundary, items remain failed/incident-blocked while the execution waits. Display the number blocked separately from successful/active/pending/cancelled items. Keep the item's node-ledger visit active while recovery remains possible, matching ordinary durable activity behavior; failure details live in job attempts/incidents. An authorized incident retry requeues the same item job or call message and restores that item's work state to active without losing successful siblings or reopening a terminal ledger visit. Mark a visit faulted/cancelled only when a boundary or terminal failure/cancellation settles it. Resume admission only after no item-level blocking incidents remain. Multiple item incidents must not accidentally unlock the body when only one is retried.

This is a known first-release limit: a failure that repeats on every attempt, such as an item the receiving API always rejects, can keep an uncaught body waiting until cancellation or an interrupting timer/conditional boundary fires, because that item's inputs stay frozen, version switching is blocked, and items cannot be skipped. Document it, recommend appropriate error handling (`retryFirst` where transient failures are expected) and a whole-body timeout/interruption when required, and keep item skipping in the parent's later releases. A non-interrupting reminder alone does not resolve the incident.

Instance cancellation, terminate/error ends, gateway branch cancellation, scoped interrupts, and parent-call cancellation must all settle the body's rows/jobs using the existing central cancellation paths: `SetInstanceTerminalStatusAsync` and `CancelDurableWorkForTokensAsync`, which already reaches `CancelOtherJobsByTokenIdsAsync` and `AbandonCallsForTokensAsync`. Item jobs and invocations carry the parent `TokenId` so those helpers fence them, and `CancelDurableWorkForTokensAsync` gains the item-row, ledger, and reservation settlement. These paths close retry authority immediately. A late successful HTTP response cannot publish locals, process variables, history, or continuation after the fence changes. Cancellation cannot undo remote side effects.

Record node-ledger lifecycle for each parent item, and body completion/boundary evidence once. Define instance-history notes for body start, caught item failure, and body completion, with bounded, value-free payloads; successful items write none. Exclude rows that record the outgoing flow from previous-actor claim and assignment inheritance, as automatic gateway rows are. An empty body leaves its start and completion rows and its flow evidence, and no item or node-activity rows. Automatic parent items have no owner, selected user action, action roles, or delegation claim. Called child workflows retain their own normal task/actor ledger. Preserve captured causal actor/audit claims where the service/script durable audit contract requires them; call execution/continuation retains its fixed actor and never inherits those claims or an operator retry's identity. Extend existing completion reasons only where the current vocabulary is insufficient, and document the resulting enum values.

Count incoming/outgoing sequence-flow traversals once for the body, never once per item delivery. Automatic advancement records its history note as the FlowInfo `kind`, so the body's outgoing traversal uses the body-completion note, never `multiInstanceOutcome`, and is attributed to the captured causal actor for service/script bodies and to `callActivity` for calls. Called workflows retain their own flow history. Update UI-14 diagram history: `InstanceDiagramHistoryQueryService.IsMultiInstance` must recognize automatic bodies, and `InstanceDiagramHistoryQueryRepository` must group `automaticActivityItem` rows by execution as it groups `userTaskItem` rows, so an incoming flow counts once per body and outgoing flows count through target entries. An empty body has no item rows: it is marked reached through its incoming flow when that flow's source is an ordinary node, whose exit is counted. After an async-after or MI source the entry is counted on the target side, an empty body leaves none, and the existing `multiInstanceEntryUnrecorded` coverage reason reports the gap. Add the new kind to the `NodeActivity.razor` kind filter. Add no human-task lifecycle webhooks for automatic parent items; actual child-instance/task events retain normal capture. Classify new job kinds in incident lifecycle capture/catalog tests without adding application-level event emission. Telemetry may include bounded node kind/status, counts, durations, and retry outcomes, never collection values, script text, request bodies, local outputs, credentials, or unbounded IDs as metric labels.

### 8. Definition validation, compatibility, and safety of loading

Update these checks together: shared JSON binding/model normalization, `WorkflowDefinitionValidator`, readiness/shared-access analysis, the editor save-validator phases, import/export normalization, and authoring validation that consumes the same server checks.

- Introduce a named automatic-MI rule helper so topology, local declarations, instance/shared source/result contracts, access permissions, async requirements, whole-body boundary eligibility, reserved names, producer separation, and expression dependencies have a single server owner. Extend timer/conditional host rules for MI calls explicitly while retaining ordinary-call restrictions and instance-only conditional predicates.
- Validate explicit malformed values before normalization can erase them; for example, `WorkflowModelMigrator` clears `cardinalityExpression` and `onePerActor` on collection sources, so the automatic-body rejections of those fields read the raw document. In the editor, preserve invalid imported MI settings and unsupported combinations for save diagnostics. Selecting a node or redrawing must not rewrite its configuration.
- Branch by activity kind when handling outputs and flow rules, and require the explicit `completionEvaluation: "afterAll"` from §2 on automatic bodies. Preserve human normalization, including legacy selectable defaults and `afterEach` behavior. New automatic settings on human MI nodes must fail explicitly rather than accidentally alter human execution.
- Extend `WorkflowModelMigrator` and the editor loader to retain service/script/call MI. Integrate call import-preservation rules so accepting MI does not clear other invalid call settings. Do not reinterpret an older arbitrary object on another node type as valid configuration. Require an explicit edit to clear unsupported settings.
- Separate local producers from process/shared producers in declared-variable inference, output target checks, `CallActivityRules.SourceDeclarations`, shared access plans, and the lock-order proof. Build the global collision set after excluding locals from every automatic-MI node, while retaining ordinary producers and automatic shared write targets. A local must not collide with itself through old producer inference, and a downstream call must not mistake it for a parent process variable. Include collection, item, aggregate, error and boundary-continuation accesses in the segment proof; check conflicts by resolved shared key as well as alias.
- Preserve ordered editor error arrays and add API/editor parity cases for missing, null, wrong type, invalid enum, unsupported field, and conflicting target combinations. Do not weaken general expression or JavaScript sandbox validation to admit MI.
- Put new editor save-validator helpers between the `// BEGIN/END WORKFLOW SAVE VALIDATOR` markers, because the Jint suites run only that span.

Until the parent's integration step, one release switch in the automatic-MI rule helper, mirrored by the editor's save validator, rejects `multiInstance` on service, script, and call nodes. While it is closed, the editor also hides the new MI controls on those nodes, so the product never offers a setting it would refuse to save; imported settings still show their save diagnostics. It is not a production setting: a host opens it only in the Development environment, through an option every other environment ignores, so no operator can enable a partial release. Both browser suites already start their published hosts in Development, so they can open it; the hosted editor receives the server's effective value through its mount options, while the standalone launcher keeps it closed. Unit and Jint tests override it directly. The integration change that records release evidence turns it on everywhere and removes the option. The existing `WorkflowDurableProcessing:PublicationEnabled` gate cannot serve, because closing it blocks every async, timer, and call definition.

**Opening by activity kind (decided 2026-11-12).** The switch above opens authoring only. The foundation adds three guards that no option opens: `WorkflowDefinitionReadinessChecker.EnsureDurablePublicationAllowed` refuses to publish any definition with an automatic body, `WorkflowEngineService.EnsureAutomaticMultiInstanceRuntimeAvailable` stops every entry path, and a leased `multiInstanceItem` job fails because no adapter is installed. The adapter subtasks need real bodies for their Worker and browser evidence before the integration step, so each one replaces these guards for its own activity kind only:

- Publication is allowed when the release switch is enabled and every automatic body in the definition has an installed adapter. The switch opens only on a Development host, so no other environment can publish a body of any kind.
- Runtime entry and item processing proceed for a kind whose adapter is installed and keep failing closed for the others. An automatic body never falls through to one ordinary activity.
- An adapter counts as installed in the change that completes its entry, item, failure, and cancellation paths, not earlier. Proposed mechanism: the core owns one list of installed kinds, and the three guards read nothing else.
- Service and script bodies share the entry (`asyncBefore`) and item (`multiInstanceItem`) routing in `WorkflowEngineService.Jobs.cs`. The first of TASK-05B and TASK-05C to land adds one dispatch by node type; the other adds its case. Calls enter through `multiInstanceStart` and their own call messages (TASK-05D).
- Each adapter's tests pin that the kinds not yet installed stay refused at publication, entry, and item processing, and that a host outside Development refuses every kind.
- The integration change removes the per-kind checks together with the option.

Implemented with TASK-05C on 2026-11-12: `AutomaticMultiInstanceRules.InstalledAdapterKinds`
is that one list and holds `scriptTask` only. All three guards also require the open
switch, so a host outside Development refuses every kind at publication, at runtime
entry, and at item processing, including a definition another host published. The
Worker resolves the option for its own environment, as the API does; its refused
entry and item jobs retry and then wait in an incident, and a retry on an open Worker
resumes them.

**Size measure (corrected 2026-11-12).** The item and body size checks first counted
compact JSON, while the tables' check constraints count `octet_length(column::text)`,
the text PostgreSQL prints for stored `jsonb` (spaces after commas and colons, numbers
in full). A value could pass the code and then break the constraint with an unmapped
error, which a Worker treats as an unexpected failure and evaluates again. Every
automatic limit now counts `JsonbText.ByteCount`, the same text, so the code always
refuses first and an oversized item takes `RefuseItemResultAsync` once.

**Script adapter decisions (agreed 2026-11-12).** TASK-05C raised five questions that
change shared contracts; each takes the proposal its plan recorded:

1. A binding that a JavaScript body's staging plan reads only because of its
   conservative scope, and that has never been set, is absent from the item context
   (`hasVariable` is false). `SharedVariableAutomaticPhaseAccessPlan.OptionalReadAliases`
   carries these; a statically named dependency still fails closed.
2. A local validation that passes at evaluation and fails against the shared values
   current at publication is the authored output failure of the saved candidate. It
   follows the node's job policy; the script is never evaluated again.
3. Each JavaScript item stores its own copy of every `config.*`/`setting.*` value in
   its staged input, within the 1 MiB item limit. No body-level copy is added.
4. The adapter lists a JavaScript body's stored instance-variable names under the
   instance lock and passes them as named inputs; no "every instance variable"
   request is added to the coordinator.
5. TASK-05C owns the local-validation language rule: a local output's validation
   rejects `FlowInfo`, the gateway helpers, and `CountFlow`/`PercentFlow`, and an
   item's expressions read no `mi.*` name except `mi.item`, `mi.index`, and `mi.total`.

For the first release, **block every workflow-version switch while an automatic MI entry/body/completion is open**, including incident-blocked work, an entry waiting for another body of its node, and its async-after continuation. Return a precise compatibility blocker identifying the node/execution. A target that adds, removes, or changes `multiInstance` on a node holding an open job is already blocked: `ValidateOpenJobs` compares the full `DurableNodeContract` before `JobDescriptorMatches`, and `CanonicalNode` includes `multiInstance`, so it reports `open_job_contract_changed`; open ordinary calls keep their unsettled-call blockers. Pin both with regression tests rather than adding a second mechanism. Do not implement partial rebinding of item jobs/snapshots. Completed executions remain immutable history and do not by themselves block switching at a later supported wait.

Preserve unsettled-call version blockers on both parent and child in addition to the MI blocker. Automatic bodies/items are not new reactivation targets; retain the existing ordinary-user-task target restriction, prohibition on reactivating linked children, and requirement to settle calls/stop descendants before root reactivation. Reaching the node again through a loop or later valid reactivation creates a fresh activation, execution, items, and job/invocation identities. Terminal-state detection must not complete an instance while an automatic body still has pending, active, or recoverable work.

### 9. API contracts and authorization

Keep recovery on existing job/incident operations; no new public complete-item, claim, assignment, or retry-all mutation route is needed. Preserve `WorkflowJobs.RequiredRole` (default `admin`) for job inspection/retry and the existing instance/node-activity visibility rules. Instance visibility never grants operational retry authority.

Proposed additive read contract:

| Surface | Plan |
| --- | --- |
| Instance detail/execution positions | Add a separate, additive collection of automatic-body progress (proposed `automaticMultiInstances`): activity kind, total, completed, active, pending, failed, cancelled, effective concurrency, and whether and why admission is blocked (item incident, queue capacity, or call capacity). `MultiInstance` and `MultiInstances` stay human-only, so existing clients and `InstanceDetail.razor` never treat a body as votable human work. Positions keep `MultiInstanceExecutionId` and add the activity kind; consumers that look up progress by that ID consult both collections. Do not embed all local results in instance list responses. |
| `GET /api/multi-instance-executions/{executionId}/items` (new) | Automatic-item summaries ordered by index, using repository pagination conventions, default page 1/page size 50, maximum 200. Return index, lifecycle, timestamps, and item/job/incident correlation without input/result payloads. Filter visibility before count/paging. A human execution's ID gets the existing not-found response. |
| `GET /api/multi-instance-executions/{executionId}/items/{itemId}` (new) | Authorized detail with copied item, local result, bounded diagnostic, and job/incident references. Require owning-instance visibility and the node-activity read scope used for execution detail; IDs from another parent are not interchangeable. |
| Job/incident and node-execution DTOs | Add nullable automatic-item correlation and the new execution kind. Preserve existing human fields and tolerate unknown future kinds in clients. |
| Existing `GET /api/instances/{id}/calls` and parent/child call summaries | Add nullable MI execution/item/index correlation to call DTOs and execution-position context; retain bounded paged summaries, pinned versions, parent/root/child links, and current instance-detail read authorization. A relationship grants no authority over child tasks or retry/cancel operations. |
| Existing `/flows` and `/roles` MI operations | Remain human-only; automatic execution IDs produce no actionable resource and no mutation. Use the existing not-found convention for an inapplicable resource. |
| Administrative actions | Batch discovery on `/administrative-actions` and `GET/POST /api/instances/{id}/administrative-actions` never list or accept automatic bodies; a request naming one gets the existing not-found response. `forceParent` and `completeAllChildren` remain human-only. |

Use authenticated GETs: unauthenticated requests return 401, missing/out-of-scope item resources return 404, malformed pagination follows existing 400 handling, and stale/ineligible retry follows existing 409 handling. A caller may see body progress without being allowed to open node-activity payload detail or Operations. UI links must respect that distinction, and the server rechecks access on every request.

Implement reads behind a focused query service/repository, with batch/grouped queries. Document exact casing/DTOs, pagination/error behavior, and generated development OpenAPI alongside endpoint registrations and authorization tests. No secret staging snapshots or HTTP authorization headers are returned by these DTOs.

A shared collection element copied into the authored result envelope or item detail is intentional workflow payload, governed by the existing instance/node-activity payload permissions, just as an explicit script copy into an instance variable is. Explain this in authoring help and test that permission boundary. Do not dump unrelated shared reads, live catalog values, staged shared write candidates or credentials into item/instance DTOs. Shared-write correlations contain identity/revision/change metadata only; direct shared catalog/history access keeps its own authorization. Explicit local outputs can expose a value only through the author's declared mapping, not by automatic copying of shared targets.

### 10. Shared editor and Flowbit.Ui

Keep authoring changes in the canonical editor assets. Add the MI section for service/script/call nodes with instance/shared collection selection, mode, maximum concurrency, local-output declarations, and instance/shared aggregate target controls; enabling MI writes `completionEvaluation: "afterAll"`. While the release switch is closed, this section stays hidden on those nodes (§8). Calls additionally offer item/index/total and shared input sources plus local/shared output targets, while keeping async/job controls unavailable. Show required binding access, snapshot timing, shared-write conflict recovery, durable execution, call-version pinning and Worker dependence in field help. Distinguish local, instance and shared values visually. Enable attachment/editing of body timer/conditional events with existing schedule, calendar, delivery-policy and `cancelActivity` controls; explain entry-condition versus initialized-body timer timing.

Enabling MI must review existing mappings/assignments and require valid local or writable shared targets; never silently redirect existing instance writes. Disabling MI or changing node type must surface/confirm incompatible settings, including MI-only call boundaries/mappings, rather than silently promoting locals to process writes or losing valid shared targets. Integrate explicit accepted changes with existing undo/history root handlers, including `changeNodeTypeFromInspector`. Preserve imported invalid values until the user edits/removes them, and preserve the hosted dirty/saved baseline and AI snapshot/apply behavior.

Render the existing sequential/parallel MI marker on service/script/call shapes, preserving the call marker as well. Add live validation and accessible labels/help. Check standalone and hosted authoring, import/export round trips, copy/paste, undo/redo, type conversion, and viewer rendering. No extra JavaScript runtime dependency or alternate editor implementation is introduced.

Update instance progress/results to show item index, state, timing, local outputs, effective concurrency, why admission is blocked, an error boundary's partial result, shared-write publication conflicts, body boundary occurrences, and authorized job/incident links. The current `InstanceMultiInstanceResults.razor` human columns (User/Action/Submitted data) remain for human MI; automatic work gets an appropriate paged presentation. `InstanceDetail.razor` keeps its interrupt-flow lookup and its claim and flow-action hiding for human MI only. Show an empty-input completion, waiting-with-incident state, loading/error/empty pages, and cancelled work clearly. Update execution-position text, node-activity filters/detail, Operations correlation, and the instance diagram without exposing claim/role/assignment buttons.

The call-specific mapping controls, invocation list, navigation, pin blockers, and cancellation notices are owned by [TASK-05D](task-05d-multi-instance-call-activities.md); consume these shared item/progress components rather than duplicating them.

Use additive DTOs in the typed UI client; clear protected detail on identity changes and reject stale page responses. Keep list queries bounded and avoid fetching one job per child. A screen showing up to 1,000 results must paginate rather than render or transfer every local payload on first load.

### 11. Adapter integration and implementation sequence

The core owns the state transition, and each adapter supplies only the activity-specific preparation/execution/delivery and failure classification. Agree repository records and method signatures before parallel adapter implementation; these are responsibilities, not an instruction to add another plugin framework.

| Stage | Core deliverable and adapter handoff |
| --- | --- |
| Contract | Freeze shared JSON, local declaration validation, immutable input/result records, item identity, actor/attempt context, and policy hooks. Land the release switch, its Development-only opening, and the validator, migrator, and editor scaffolding for all three activity types, so B/C/D add rules to existing seams instead of editing the same blocks in parallel. Service/script use `asyncBefore`; calls supply intrinsic entry/pinning without authored async/job settings. |
| Persistence | Add body/item/wait/job/ledger constraints and repositories, capacity reservation, and retention rules. TASK-05D supplies invocation/FK/pin amendments before final migration review; do not independently edit the EF snapshot in parallel. |
| Coordinator | Implement initialization, shared snapshots and publication, bounded admission, item-state transitions, dispatch wake-up, aggregate publication and body boundaries/cancellation. Adapter completion returns candidate locals/shared writes or typed failure; only the coordinator commits them and owns token continuation. |
| Recovery | Integrate output fences, aggregate incidents, conditional drains, loop guards, open-version blockers, terminal-state checks and cancellation across adapters. |
| Queries/UI | Implement grouped progress, paged item endpoints/DTOs, node-ledger integration, shared local-output inspector and runtime result display; adapters add type-specific controls. |
| Integration | Exercise real B/C/D implementations against this same coordinator and schema. Core unit tests may use test adapters, but they do not fulfill runtime or browser release gates. |

Principal owners: `Flowbit/src/Flowbit.Shared/Models/WorkflowModel.cs` and `WorkflowModelMigrator.cs`; service `WorkflowDefinitionValidator`, readiness/shared-access/producer analysis, new automatic-MI rule/coordinator helpers, `WorkflowEngineService.cs`/`.Jobs.cs`, `WorkflowJobCapacity`, persistence/job records and repository ports; infrastructure entities, `Data/AppDbContext.cs`, migrations, `WorkflowRuntimeRepository`, `WorkflowJobRepository`, and retention repositories. Projections/queries use `WorkflowInstanceProjectionService`, `RuntimeProjectionMapper`, `NodeExecutionQueryService`, shared DTOs and MI/job/node endpoint registrations. Editor/UI owners are the canonical editor assets, typed client, instance-detail components, node activity and Operations. Also in scope: the multi-instance execution readers listed in §5, including `AdministrativeActionCandidateRepository` and `InstanceDetail.razor`, `RetentionRepository`, `WorkflowVersionCompatibilityEvaluator`, `InstanceDiagramHistoryQueryService` and its repository, `NodeActivity.razor`, and the AI authoring package (§13).

### 12. Tests and verification

| Area | Required core coverage |
| --- | --- |
| Compatibility | Existing human collection/cardinality/one-per-actor/outcome behavior and result formats; ordinary service/script/call behavior unchanged; every multi-instance execution reader in §5 keeps its human results, existing human-MI DTO fields never carry a body, and administrative actions never offer automatic bodies; the release switch rejects automatic MI and hides its controls outside Development hosts, and other environments ignore its option; API/editor parity and ordered error arrays. |
| Input/local/shared contract | Every instance/shared element type; duplicates/order; empty/missing/unset/null/non-array/limit+1; explicit `afterAll`; readable sources and readWrite outputs/aggregate/error targets; reset validation; source/target collisions by alias and key; bounded copied/candidate payloads; frozen collection/instance inputs versus per-item shared/config/time staging; sequential observation and parallel conflicts. |
| Shared publication | Multiple workers/instances writing one key, identical-value versus effective changes, archived/contract-changed bindings, mixed local/shared batch rollback, current validation dependencies, ordinal lock proof including refill/boundary continuations, conflict recovery accepting saved stale candidates without reinvocation/recalculation, cancellation fencing and exactly one shared write correlation per accepted item. |
| Body boundaries | All three activity kinds and both cancelActivity modes; condition true at entry before body creation, second token waiting for another body, timer starts only at initialization, duration/date/cycle/calendar rules, conditional atomic/durable delivery and false-to-true rearm, restarts/recurrence/capacity, incident waits, competing error/timer/condition/final result, no new aggregate on interruption, accepted non-interrupting delivery after host exit, no subscriptions through asyncAfter, and child ancestry cancellation. |
| Admission | Multiple Workers and multiple bodies; sequential window; default and cap settings, their parse/fallback/cap rules, and the reported effective value; setting changes affect new bodies only; blocked-item slots; reserved dispatch under full queues, restart, cancellation and lease loss; the nothing-in-flight incident at a lowered `MaxInstances`; a second token's entry waiting for its node's open body; no leaked reservation or lost wake-up. |
| Completion | Out-of-order saved results; simultaneous last items; one aggregate/traversal; version-fence conflict retried without adapter reinvocation; aggregate validation and size failures take the boundary or fault the instance with their stable codes; partial result on a caught failure, including its fence-conflict case with siblings already stopped and its size case; bodies of one node publishing one after another; empty-body completion. |
| Races | Crash around entry/staging/resultReady/item commit/aggregate; stale leases/activations; duplicate finalization; conditional cancellation from reset/publication; cancellation versus success and operator retry. |
| Lifecycle | Instance/terminate/error/scope/parent-call cancellation; immutable completed siblings; active ledger during recoverable incidents; loop limits; no premature terminal instance; version/reactivation blockers, including a target that adds or removes MI on a node with an open ordinary job. |
| Queries/auth | SQL count/order/paging, grouped bounded reads, no automatic parent items in inbox/claims/roles, node-kind/history accounting, diagram-history counts for automatic and empty bodies, 401/404/409, payload-detail versus progress/Operations permissions and stale UI identity responses. |
| Migration/retention | Fresh and populated databases; human and ordinary-call preservation; FK/unique/index enforcement, including the one-open-body index and the widened check constraints; a body older than the completed-job period keeps its snapshot and item results after its entry and item jobs are pruned; compact call-linked item identity; history pruning and queue cleanup races. |

Extend `MultiInstanceApiRegressionTests`, `MultiInstanceBulkPersistenceTests`, durable repository/processor/conflict/finalization and loop-guard suites, `WorkflowVersionCompatibilityEvaluatorTests`, projection/node-execution tests, `AdministrativeActionCandidateExclusionApiTests`, `InstanceDiagramHistoryQueryServiceTests`, `UserTaskRoleManagementServiceTests`, shared access-plan tests, retention/reactivation tests, `WorkflowAuthoringPackageTests`, `EditorValidatorTests`, and `EditorValidatorCharacterizationTests`. Add targeted automatic-MI coordinator/query cases. Activity-specific scenarios belong to B/C/D.

Use the [parent validation gates](task-05-multi-instance-automatic-activities.md#validation-and-release-gates) for commands and final evidence. Common browser scenarios exercise shared collection/output/aggregate authoring, timer/conditional attachments in both modes, queued-entry and incident-time interruption, recurring reminders, saved-candidate recovery without reinvocation, invalid imports/type changes/undo, paged running/blocked/completed/cancelled results, protected-detail permission changes, and version blockers at 1440×900, 1024×768 and 390×844. Durable runtime scenarios use real adapters and the Worker acceptance fixture; unit stubs cannot replace that gate. Until the integration step, these scenarios open the release switch on the Development test hosts; the standalone editor stays closed, so it covers the hidden controls and import diagnostics. The integration change reruns them with the switch on by default.

### 13. Documentation and completion handoff

Time each documentation update to what users can do. Objects that ship while the release switch is closed are documented when they ship and marked inactive until automatic multi-instance is enabled:

- tables, columns, and constraints in [database schema](../database-schema.md);
- the two concurrency settings, with their parse, fallback, and cap rules, in [engine settings](../engine-settings.md), and their operational behavior in [deployment](../deployment.md);
- new routes and DTO fields in the [API reference](../api-guide.md);
- implementation invariants in [AGENTS.md](../../AGENTS.md).

Authoring and behavior guides change only in the integration change that turns the switch on: [node reference](../node-reference.md), [BPMN support](../bpmn-support.md), [developer guide](../developer-guide.md), [UI guide](../ui-guide.md), [runtime reference](../../Flowbit/README.md), the examples, and the AI [authoring guide](../../authoring/flowbit-authoring/references/authoring-guide.md) and `SKILL.md`, which must drop the statements that a call cannot use `multiInstance` and that collection MI needs `string[]`. The AI authoring package bundles every guide under `docs/` except `docs/enhancements/`, `docs/plans/`, and `docs/refactoring/`, plus the runtime reference, so describing the feature earlier would tell the assistant to author definitions that validation still rejects; record the contracts in this plan until then. The package's generated `workflow.schema.json` follows the model, so it shows the new `multiInstance` fields as soon as 05A changes the model; update `WorkflowAuthoringPackageTests` in that change. These are guides, not the AI instructions or tool descriptions, which stay unchanged. Activity owners supply their specific examples/rules, and the parent owns combined rollout.

Before marking TASK-05A Done, record implemented interfaces/schema, migration sequence, exact shared test/browser results, and adapter integration evidence here. Update this status in the parent and backlog tables together. A completed foundation alone does not complete TASK-05 or authorize a partial first release.

## Implementation record — 2026-11-11

The shared foundation is implemented behind the temporary release guard. The
status remains **In progress** because the adapter integration and automatic
runtime/browser release gates above require TASK-05B/C/D. Development can open
the authored contract and editor controls; publication and activity execution
remain rejected. Ordinary human MI, service/script work and calls retain their
existing entry paths.

| Area | Implemented handoff |
| --- | --- |
| Contract | `AutomaticMultiInstanceRules`, explicit local declarations, bounded concurrency, raw-import preservation, server/editor validation, generated authoring schema, and Development-only authoring capability. |
| Persistence | `IAutomaticMultiInstanceRepository`, body-owned immutable snapshot, indexed item rows, publication candidates/results, completion intent, wait/job/ledger ownership, call-item/pin fields, reserved capacity, retention protection, and human-reader isolation. |
| Coordinator | `AutomaticMultiInstanceCoordinator` captures locked body inputs and per-item context, admits ascending windows, saves candidates separately from publication, fences shared writes, deduplicates dispatch/completion, parks capacity and occupied-entry waits without new activity attempts, and assembles bounded index-ordered results through a result-only query. |
| Graph lifecycle | `IAutomaticMultiInstanceLifecycle` binds initialization timers, notification draining, subscription closure, call cancellation and success/error continuation to the existing engine transaction. A captured failure stops siblings before the completion phase publishes its partial result. Dispatch/completion are routed by the job processor; item execution remains the adapters' responsibility. |
| Shared access | Exact initialization, staging, item-publication, aggregate and selected-error phases participate in the monotonic lock proof. The collection and same-key aliases use retained body values after initialization. Retry keeps staged inputs and saved candidates; explicit recovery refreshes output fences without recalculation. |
| Evidence and queries | Automatic items have their own node-ledger kind, without a parent visit or human tasks/policies. Body settlement and outgoing traversal evidence are separately labelled; traversals retain the body-completion FlowInfo kind. Grouped progress, authorized paged item reads, job/call correlations, version blockers and diagram history handle automatic bodies explicitly. |
| Presentation | The canonical editor has local-output/concurrency controls and markers. Instance detail has a separate paged automatic-item component with protected details and stale-identity protection; human actions remain human-only. |

The migration sequence is `20261111144523_AddAutomaticMultiInstanceCore`,
`20261111144824_ValidateAutomaticMultiInstanceConstraints`, and
`20261111192010_HardenAutomaticMultiInstanceCore`. The second migration
validates the widened large-table checks outside a transaction; the third, from the
open-findings corrections below, restores the general job index and adds the call-pin
and invocation item-pair checks. See the owning
[schema](../database-schema.md), [API](../api-guide.md),
[settings](../engine-settings.md#automatic-multi-instance-concurrency-foundation),
and [deployment](../deployment.md#automatic-multi-instance-foundation) references.

Adapter handoff remains explicit: B/C supply request/evaluation preparation and
typed failure classification; D supplies intrinsic call entry, pinning, call
admission and child result delivery. They must commit a stage before work and a
candidate before publication, use the common activation/item/lease fences, and
invoke the shared graph lifecycle rather than inventing a second coordinator.
Core repository tests may seed owned runtime rows; browser release evidence
must create real bodies through those adapters and must not manufacture rows.

Initialization resolves the concurrency settings against the newly frozen item
count, so an earlier collection-size estimate cannot narrow the persisted window.
Error routing uses the saved winning route and current completion/body/token/lease
fence, locks the selected error phase's live dependencies, restores the frozen
collection aliases, and validates the declared target before writing. It does not
add a separate error-value snapshot or version policy. An uncaught deterministic
aggregate failure uses token termination reason `multiInstanceFailed`.

### Review corrections

The ten implementation-review findings are addressed in the common foundation:

1. Item and aggregate retries retain saved output fences for ordinary incidents.
   Only `output_version_conflict` recovery authorizes accepting current versions.
2. `AutomaticMultiInstanceItemFence` supplies invocation-aware callStart/callResume
   access to the same staging, candidate, publication and failure operations.
   Parent activation, invocation/item ownership, child result and message lease
   are rechecked. `HasCurrentOwnerAsync` reads persisted ownership before reusing
   a saved item stage, even when a runtime token remains tracked from an older
   phase. The adapter owns call settlement and message acknowledgement;
   calls cannot use a generic `multiInstanceItem` wrapper. Call admission and
   end-to-end delivery/recovery remain TASK-05D integration work.
3. Initialization recaptures FlowInfo from current persisted summaries under the
   body initialization transaction. The bounded body snapshot retains it once;
   each item context exposes a separate typed copy without rereading summaries.
4. Each execution attempt rebuilds local defaults from retained inputs and its
   current job context. Shared/config/time inputs and output fences stay frozen.
   Calls receive no job-attempt or caller-claim context.
5. Saved boundary routes accept the definition's integer IDs, including zero;
   common failure capture imposes no extra positive-ID restriction. The selected
   route is still validated against the definition during continuation.
6. Item visits copy the parent's incoming flow and gateway branch. Grouped
   diagram history records one incoming traversal for a nonempty body; empty
   bodies retain the documented possible evidence gap.
7. Item completion/failure records preserve the captured causal actor, roles,
   selected audit claims and delegation. Calls retain the fixed system actor.
   Cancellation does not rewrite already settled item history.
8. Shared item writes reference the retained item node visit, making them visible
   in node-execution detail. Pruned visits remain optional; durable candidates
   and results survive independently.
9. Execution-position correlation includes activation ID, preventing a new
   entry from inheriting an older settled body's progress.
10. Progress batch-loads current control incidents and exposes payload-free
    `controlJobId`, `controlIncidentId` and `controlIncidentType`. The UI displays
    **Needs attention** even when all items are complete and no item failed.

Regressions are in the coordinator, completion, persistence and UI test classes,
including their `*RegressionTests.cs` partials. They exercise PostgreSQL writes,
retained candidates, incoming history grouping, node-execution shared changes,
activation reentry, recovery and stale call fences. No new migration is needed
for these corrections. Publication/readiness/runtime guards remain closed, and
TASK-05A remains **In progress** until the genuine B/C/D integration gates pass.

### Verification before the review corrections

The following records describe the earlier implementation. Verification of the
review corrections is recorded separately below; earlier evidence is retained
to distinguish common regression checks from unavailable adapter acceptance.

- Full solution regression: **4,828 passed, 0 failed, 0 skipped**, in 18 minutes
  51 seconds. Command: `dotnet test Flowbit/Flowbit.slnx --no-restore --nologo --verbosity quiet --logger "trx;LogFileName=automatic-mi-verified.trx"`.
  TRX: `Flowbit/tests/Flowbit.Tests/TestResults/automatic-mi-verified.trx`.
  This run includes the corrected test cleanup for body-owned snapshots and
  retired fixture jobs. The final isolated automatic-core/OpenAPI refinements
  landed during this run and are covered by the 320-test final-source run below.
- Final current-source shared-core/API and durable regression verification:
  **320 passed, 0 failed, 0 skipped**. This includes all automatic-MI tests,
  authoring-package/model checks, the four affected durable integration classes,
  and OpenAPI contracts after the final concurrency/error-routing review fixes.
  Command: `dotnet test Flowbit/tests/Flowbit.Tests/Flowbit.Tests.csproj --no-restore --filter "FullyQualifiedName~AutomaticMultiInstance|FullyQualifiedName~WorkflowAuthoringPackageTests|FullyQualifiedName~MigrationModelSnapshotTests|FullyQualifiedName~DurableAutomaticLoopGuardIntegrationTests|FullyQualifiedName~DurableAutomaticLoopGuardResetIntegrationTests|FullyQualifiedName~DurableWorkflowProcessorTests|FullyQualifiedName~DurableParallelFinalizationTests|FullyQualifiedName~OpenApiContractTests" --artifacts-path artifacts/task05a-current-tests --logger "trx;LogFileName=automatic-mi-current-final.trx" -v quiet -m:1`.
  TRX: `Flowbit/tests/Flowbit.Tests/TestResults/automatic-mi-current-final.trx`.
  The focused boundary/shared subset passed **45/45**, including 16 new scenarios.
- Earlier combined verification: **264 passed, 0 failed, 0 skipped**, including
  automatic MI, authoring-package and migration-model checks. Command:
  `dotnet test Flowbit/tests/Flowbit.Tests/Flowbit.Tests.csproj --filter "FullyQualifiedName~AutomaticMultiInstance|FullyQualifiedName~WorkflowAuthoringPackageTests|FullyQualifiedName~MigrationModelSnapshotTests" --artifacts-path artifacts/task05a-current-tests --logger "trx;LogFileName=combined-mi-current.trx" -v quiet -m:1`.
  The TRX is `Flowbit/tests/Flowbit.Tests/TestResults/combined-mi-current.trx`.
  An earlier external-artifact run passed the 261 feature tests but failed three
  authoring-package source-discovery tests because its output had no repository
  ancestor; the repository-contained run above resolves that fixture condition
  without changing the tests.
- Current common definition/coordinator/shared-access/DTO checks: **121 passed**.
  This focused set covered the definition, coordinator, exact shared-access,
  DTO, preserved-invalid-import and existing shared-access-plan suites.
- PostgreSQL completion checks: **9 passed**, including empty input, reset-triggered
  interruption, saved-result conflict recovery, stopped siblings before partial
  publication, duplicate entry, source capture and continuation rollback. The
  current coordinator and persistence sets also passed **34** and **11** tests.
  Filters: `FullyQualifiedName~AutomaticMultiInstanceCompletionTests`,
  `FullyQualifiedName~AutomaticMultiInstanceCoordinatorTests`, and
  `FullyQualifiedName~AutomaticMultiInstancePersistenceTests` on the same test project.
- Editor/UI/display tests: **779 passed**. Existing conditional/timer/durable
  regressions: **45 passed**. New boundary/cancellation and ordinary-call checks:
  **35 passed**. Fresh/populated migration, snapshot parity and historical schema
  fixture regressions passed; additive body storage preserves ordinary calls and
  the existing 1,000-item human fan-out.
- Real Chromium **151.0.7922.34** editor checks: **3 passed**, with real field edits,
  help, local declarations/export, concurrency errors, timer attachment/undo and
  standalone import diagnostics. Command: `dotnet test Flowbit/tests/Flowbit.BrowserTests/Flowbit.BrowserTests.csproj --filter FullyQualifiedName~AutomaticMultiInstanceEditorBrowserTests`.
  The final viewports/localhost hosts were **1440×900** at
  `http://127.0.0.1:64443`, **1024×768** at `http://127.0.0.1:64433`, and
  **390×844** at `http://127.0.0.1:64419`. Browser diagnostics were empty in all
  three runs. Screenshots, standalone results and JSON evidence are in
  `artifacts/browser/automatic-mi-editor/20261111-153416-0f149b4d/`,
  `20261111-153408-64e9dcfb/`, and `20261111-153355-227b7dc7/` respectively.
- Full real-browser smoke regression: **128 passed, 0 failed, 0 skipped** in
  21 minutes 40 seconds. Command: `dotnet test Flowbit/tests/Flowbit.BrowserTests/Flowbit.BrowserTests.csproj -c Release --no-build --no-restore --logger "trx;LogFileName=task05a-smoke.trx" --results-directory artifacts/browser/test-results`.
  The main published-host run used UI `http://127.0.0.1:64817`, API
  `http://127.0.0.1:64815`, and standalone editor `http://127.0.0.1:64819`;
  evidence is in `artifacts/browser/runs/20261111-154003-ca825e2c/` and the TRX
  in `artifacts/browser/test-results/task05a-smoke.trx`.
- Worker-enabled browser acceptance: the full run finished **118 passed, 5 failed,
  0 skipped (123 total)** in 34 minutes 29 seconds. Command:
  `dotnet test Flowbit/tests/Flowbit.BrowserAcceptanceTests/Flowbit.BrowserAcceptanceTests.csproj -c Release --no-build --no-restore --logger "trx;LogFileName=task05a-acceptance.trx" --results-directory artifacts/acceptance/test-results`.
  All five failed cases subsequently passed in targeted runs. The initial
  failures were slow Operations loading (both API responses were 200), an expired
  test response gate (the held upstream response was 200), one local HTTP timeout
  on a third retry, and two stale timestamp selectors. The existing `LocalTime`
  component renders `time`; the latter tests incorrectly selected `span`. Only
  that tag selector was corrected, preserving the exact timestamp assertion.
  No application behavior, timeout, or assertion requirement was weakened.
  The targeted runs use the same project, Release/no-build/no-restore flags and
  results directory, with these filters and TRX names:

  | Filter (`--filter`) | Result | TRX (`--logger "trx;LogFileName=..."`) |
  | --- | --- | --- |
  | `FullyQualifiedName~CronScheduleAcceptanceTests` | 1/1 passed, unchanged | `task05a-cron-retry.trx` |
  | `FullyQualifiedName~A8_RealResponseGatesExposeInitialHistoryAndItemLoading` | 3/3 passed, unchanged | `task05a-loading-retry.trx` |
  | `FullyQualifiedName~ContractEndKeepsItsSavedScheduleAcrossAVariableEditAndWorkerRestart\|FullyQualifiedName~DurableRetryFirstRetriesEveryFailureThenUsesTheFinalAttempt` | 6/6 passed | `task05a-timer-retry-verification.trx` |

  Main evidence is in `artifacts/acceptance/runs/20261111-155140-91501a23/`
  (UI `http://127.0.0.1:50231`). Rerun evidence is in
  `20261111-160416-c3a1bdec/`, `20261111-161140-64990f9b/`, and
  `20261111-162740-51f3e94c/` respectively. The final six scenarios used Chromium
  **151.0.7922.34**, UI `http://127.0.0.1:54112`, and 1440×900/390×844 viewports;
  all six had zero page errors, console errors/warnings, or failed browser requests.
  These are regression results for existing Worker behavior, not automatic-adapter
  execution evidence.
- The two item routes, parameters/defaults, response schemas and Bearer security
  were compared with live Development OpenAPI at
  `http://127.0.0.1:50221/openapi/v1.json`; the captured contract is in
  `artifacts/task05a-openapi-item-contract.json`. Final-source OpenAPI tests also
  verify that item-route descriptions allow settled bodies while human interrupt
  routes retain their active-execution wording.
- Documentation validation checked **1,686 relative links/anchors** and **23 JSON
  fixtures** with no errors. `git diff --check` was clean.

These common checks do **not** claim automatic REST/script/call execution or
automatic-item runtime UI acceptance. Those genuine Worker/browser scenarios
remain integration gates for B/C/D, with the release guards closed meanwhile.

### Review-correction verification

- Full solution regression: **4,878 passed, 0 failed, 0 skipped**, in 14 minutes
  13 seconds. Command: `dotnet test Flowbit/Flowbit.slnx --no-restore --nologo -v quiet -m:1 --logger "trx;LogFileName=task05a-ten-fixes-full.trx"`.
  TRX: `Flowbit/tests/Flowbit.Tests/TestResults/task05a-ten-fixes-full.trx`.
  This build preceded the final isolated causal-actor fallback, tracked-owner
  check, optional call-index correction and incident text-color adjustment.
  The final-source focused run below covers those refinements; the UI browser
  checks use the final rendered markup.
- Final-source focused regression passed **329/329**, including PostgreSQL,
  stale tracked-token ownership, both call transports, candidate publication,
  local retry defaults, FlowInfo retention, audit attribution, current incident
  projection, UI rendering, OpenAPI, authoring-package and migration-model checks.
  The real repository call test commits the child-owned message lease and saved
  candidate before publication, verifies publication-fence acknowledgement and
  adapter-owned invocation/message settlement, and rejects replay. Invocation
  ownership uses the stable item FK, independent of optional item-index read
  enrichment.
  Command: `dotnet test Flowbit/tests/Flowbit.Tests/Flowbit.Tests.csproj --no-restore --filter "FullyQualifiedName~AutomaticMultiInstance|FullyQualifiedName~OpenApiContractTests|FullyQualifiedName~InstanceProjectionTests|FullyQualifiedName~WorkflowAuthoringPackageTests|FullyQualifiedName~MigrationModelSnapshotTests" --artifacts-path artifacts/task05a-current-tests --logger "trx;LogFileName=task05a-ten-fixes-final.trx" -v quiet -m:1`.
  TRX: `Flowbit/tests/Flowbit.Tests/TestResults/task05a-ten-fixes-final.trx`.
- The real published API/UI runtime browser regression passed **7/7**:
  `dotnet test Flowbit/tests/Flowbit.BrowserTests/Flowbit.BrowserTests.csproj -c Release --no-restore --filter "FullyQualifiedName~InstanceDetailSmokeTests|FullyQualifiedName~RuntimeSmokeTests" --logger "trx;LogFileName=task05a-ten-fixes-browser.trx" --results-directory artifacts/browser/test-results -v quiet`.
  Both hosts were freshly published from the corrected source. Chromium
  **151.0.7922.34** used UI `http://127.0.0.1:55293`, API
  `http://127.0.0.1:55257`, and 1440×900, 1024×768 and 390×844 viewports.
  Real interactions covered inbox claim/completion, instance navigation,
  replacement identity, gateway and human-MI detail, polling and responsive
  layouts. All scenarios had no page errors, console errors/warnings or failed
  requests. Evidence: `artifacts/browser/runs/20261111-171232-c73613e1/`.
- The changed automatic progress markup was separately rendered from the actual
  Razor component with explicit fixture DTOs and served at
  `http://127.0.0.1:55197`. Chromium **151.0.7922.34** verified the incident type
  and IDs, keyboard focus and horizontal table scrolling at **1440×900**,
  **1024×768**, and **390×844**, with no console errors/warnings or page overflow.
  All three screenshots were visually inspected. Evidence and screenshots:
  `artifacts/task05a-fix-preview/evidence.json` and `progress-{width}.png` in that
  directory. This is isolated component layout verification, **not** an automatic
  execution acceptance test. No production guard was disabled and no application
  state or database rows were fabricated for browser runtime acceptance.
- Documentation validation: **1,828 relative links/anchors** and **23 JSON
  fixtures**, no errors, using `node artifacts/acceptance/validate-doc-links.cjs`.
  `git diff --check` passed; Git reported only existing line-ending conversion
  notices. The build retains existing package/analyzer warnings.

### Second review corrections — 2026-11-11

A later review found three defects, corrected here:

1. **Editor type change.** Changing a multi-instance user task into a service or
   script task kept its human configuration and outcome flows. With the release
   switch closed, save then refused the node and the inspector offered no way to
   remove the setting. A type change now never turns one multi-instance contract
   into another: it removes the human configuration as it did before TASK-05A, and
   an automatic body still needs its own explicit enable. A service or script body
   imported while the switch is closed shows **Remove unsupported multi-instance**,
   as calls already offer **Remove these options**.
2. **Async-after retry.** A body's `asyncAfter` job names the body but runs after
   it settles, so retrying its incident always failed with "no longer current".
   `RetryIncidentAsync` now checks the open body only for `multiInstanceItem`,
   `multiInstanceDispatch` and `multiInstanceComplete` jobs.
3. **Call-item cancellation.** Abandoning a running call item queued a `callCancel`
   job carrying the item id, which `CK_workflow_jobs_automatic_item_shape` refuses,
   so the cancellation transaction failed. The message now correlates only through
   its invocation, as §5 specifies for call messages.

Each correction has a regression test that fails against the previous code:
`EditorAutomaticMultiInstanceTests` (type change, removal, open-switch section),
`AutomaticMultiInstancePersistenceTests.Async_after_incident_of_a_settled_body_is_retried_as_an_ordinary_job`,
and `AutomaticMultiInstanceCallCancellationRepositoryTests.Abandoning_a_running_item_call_queues_a_constraint_valid_cancel_message`,
which drives the engine's own abandonment path against PostgreSQL. With the
previous code restored, 7 of the 8 new cases failed for the expected reasons
(the human-to-call case already passed, because a call clears it).

Verification:

- Full solution regression: **4,889 passed, 0 failed, 0 skipped**, in 11 minutes
  59 seconds. Command: `dotnet test Flowbit/Flowbit.slnx --no-build -m:1 --logger "trx;LogFileName=task05a-fixes-full-2.trx"`.
  An earlier full run failed one unrelated list test because the new cancellation
  test committed a child instance without an execution token; the child now has one.
- Focused regression: **1,313 passed, 0 failed**. Command:
  `dotnet test Flowbit/tests/Flowbit.Tests/Flowbit.Tests.csproj --no-build -m:1 --filter "FullyQualifiedName~Editor|FullyQualifiedName~AutomaticMultiInstance|FullyQualifiedName~CallActivit|FullyQualifiedName~DurableJobRepository|FullyQualifiedName~WorkflowJobOperations|FullyQualifiedName~DurableWorkflowProcessor|FullyQualifiedName~Incident|FullyQualifiedName~Retry" --logger "trx;LogFileName=task05a-fixes-focused.trx"`.
- Chromium: `AutomaticMultiInstanceEditorBrowserTests` and
  `InstanceDiagramFitBrowserTests`, **6 passed**.
- Real browser (built-in Chromium pane), standalone editor served from the source
  assets at `http://localhost:5599`. At 1024 wide: a multi-instance user task
  changed to a service task through the **Type** selector keeps one outgoing flow,
  save reports only the new task's missing URL, and **Undo** restores the user task
  and both outcome flows. An imported service body shows the warning and button;
  clicking it removes the setting and its marker, and **Save** passes validation.
  At 375×812 the warning wraps inside the inspector, the button fits, the page does
  not overflow, and the button works. The console was empty throughout.
- Documentation links: **1,829 relative links/anchors** and **23 JSON fixtures**,
  no errors; `git diff --check` was clean.

The same review recorded further findings, addressed in the next section.

### Open-findings corrections — 2026-11-11

Each finding the review left open is corrected or narrowed to the part TASK-05A owns:

| Finding | Correction |
| --- | --- |
| Call-body pin never written | `AutomaticMultiInstanceCreateRecord` carries the pin and the repository stores it with `PinState` `pinned`, released at settlement. A nonempty call body requires it and every other body refuses it, in the repository and in `CK_multi_instance_executions_call_pin_shape`, so the unpublish guard covers a body before its first child. Resolving the pin stays with TASK-05D. `AddInvocationAsync` records item ownership, `CK_call_activity_executions_automatic_item_pair` keeps the pair whole, and `GetForActivationAsync` reads ordinary calls only. |
| No fence refresh for a call item's `callResume` | `RetryIncidentAsync` treats a `callResume` whose invocation owns an item with a saved candidate as publication-only: it keeps the result and sets `PublicationFenceRefreshRequested` only for `output_version_conflict`. It reads, never locks, the parent rows. |
| Over-limit item result without a boundary | `AutomaticMultiInstanceCompletionIntent.BoundaryNodeId` is optional. `RefuseItemResultAsync` stops the body like a caught failure without choosing a route; completion then publishes nothing and takes the catch-all error boundary or faults with `multiInstanceFailed`, as an unacceptable aggregate does. The refused item's visit ends `multiInstanceInterrupt`. |
| Engine callers and fake-only tests | Item blocking is derived from open incidents on an item's `multiInstanceItem` job or a call item's `callStart`, so it holds whichever path opened the incident (fenced processing, the lease sweeper, or direct enqueue) without writing body rows from job-side locks. `AutomaticMultiInstanceProcessorTests` drives dispatch and completion through the registered `IWorkflowJobProcessor` and the real engine continuation: an empty body completes its instance, and a refused result faults it or takes the catch-all boundary. Routing the entry job to initialization and item jobs to execution remains adapter work (TASK-05B/C/D), as the plans assign. |
| Human cancellation not kind-filtered | `CancelActiveMultiInstancesForTokensAsync` touches `userTask` rows only; automatic bodies settle through their own repository. |
| Settled payloads kept forever | Settlement clears items' staged inputs and candidates. Workflow-history retention, when it prunes a finished instance, empties each settled body's input snapshot beside the settled call snapshots it already clears; item results stay. |
| Local outputs counted as stored variables | `ConditionalEventDefinitionAnalyzer.DeclaredStoredVariableNames` (also used by expression timers), the inbox-visibility symbol table and its editor mirror, and version-compatibility target declarations skip an automatic node's own local outputs. |
| Editor switch from the UI host | `GET /api/workflows/authoring-capabilities` reports the API's effective switch; `WorkflowEditor.razor` mounts the editor with it and fails closed. |
| Read surfaces | Instance-list, version-change and variable-update positions name the open body of their own activation with its kind. The version blocker names the body's execution id. Instance detail polls while a body is open. The diagram gives an automatic body an **Automatic items** progress badge and lists its items, and position text treats a settled body's async-after wait as background work. |
| Dropped job index | The hardening migration restores `IX_workflow_jobs_MultiInstanceExecutionId`. |
| Schema documentation | The per-table sections of the [database schema](../database-schema.md) list the new kinds, wait state, columns, keys and checks. |

Verification of these corrections:

- Full solution regression: **4,905 passed, 0 failed, 0 skipped**, in 13 minutes
  10 seconds. Command: `dotnet test Flowbit/Flowbit.slnx --no-build -m:1 --logger "trx;LogFileName=task05a-open-findings-full.trx"`.
  After the documentation edits, the documentation-reading tests (authoring package
  and schema) passed **106/106** on a rebuild.
- New tests: `AutomaticMultiInstanceProcessorTests` (3), pin and invocation-pair
  constraints, call-item delivery retry (3 cases), item-incident blocking, list
  position correlation, retention payload clearing, local outputs in conditional,
  inbox and version-compatibility analysis (server and editor), the open-body
  blocker id, the automatic diagram badge, and the authoring-capabilities endpoint.
- Chromium against freshly published API/UI hosts: `HostedEditorSmokeTests`,
  `EditorSmokeTests`, `AutomaticMultiInstanceEditorBrowserTests`,
  `InstanceDetailSmokeTests` and the instance-diagram suites, **76 passed**
  (evidence in `artifacts/browser/runs/20261111-200045-39588e93/`).
- Real browser (built-in Chromium pane) at 1440×900, published API on
  `http://127.0.0.1:5617` and UI on `http://127.0.0.1:5652` in Development, over a
  fresh PostgreSQL that applied all three migrations (the hardening migration's
  checks and index confirmed in the catalog). With the API option set, selecting a
  service task in `/workflows/new` showed **Automatic multi-instance**; after
  restarting only the API without it, the same page hid the section although the UI
  host still had the option set. Each load called
  `GET /api/workflows/authoring-capabilities` (200); the console was empty.
- Documentation links: **2,016 relative links/anchors** and **23 JSON fixtures**,
  no errors.

Still open after these corrections:

- Adapter integration owned by TASK-05B/C/D: initializing a body from its entry job, executing item jobs, resolving a call body's pin, call admission and result delivery. Each adapter also opens the publication, runtime, and item-job guards for its own kind, as §8 records under **Opening by activity kind**.
- Review findings outside the recorded list: per-item cost (each item completion reads progress for every body in the instance and lists its open jobs); editor/server differences in first-error order, wording, and acceptance of a local `defaultValue` written as a string; local-output `validation` accepting `FlowInfo` and gateway functions (closed by TASK-05C on 2026-11-12, decision 5 above); disabling multi-instance or changing type not reviewing call-only mappings; **Remove these options** on a call also removing a valid automatic body; a falsy imported `multiInstance` loading as an enabled one; sparse inspector help; a possible one-slot capacity over-credit during completion; the first migration's validated foreign keys and indexes on large tables inside its transaction; no shape check for `multiInstanceStart`; and the authoring-side test gaps listed in §12.
