# TASK-05D: Multi-instance call activities

[Parent TASK-05](task-05-multi-instance-automatic-activities.md) · [Enhancement backlog](README.md) · [Documentation home](../index.md)

| Field | Value |
| --- | --- |
| Status | In progress |
| Category | New task type |
| Effort | L |
| Priority | Required for TASK-05 first release |
| Parent | [TASK-05](task-05-multi-instance-automatic-activities.md) |
| Depends on | [TASK-05A: shared automatic multi-instance core](task-05a-multi-instance-core.md), [TASK-01: call activity](task-01-call-activity.md) (Done) |

## Summary

Run a called workflow once for each item in an immutable collection, sequentially or within a persisted parallel window. Every item owns one durable call invocation and at most one child instance. Child results become item-local outputs or authorized parent shared-variable writes; only the shared coordinator publishes the ordered result collection and continues the parent. Shared-variable access and whole-body timer/conditional boundaries are part of the first release.

**Multi-instance calls are required in TASK-05's first release.** The call adapter is implemented behind the Development-only release switch; see the [implementation record](#implementation-record-2026-11-13) for current behavior, verification, and what is [still open](#still-open). TASK-05D remains **In progress** until those items are complete. The plan below was written from repository inspection on **2026-11-11** and reviewed on **2026-11-13** against the code at commit `a4b5523`, after the foundation and the script and REST adapters landed; the [plan review](#plan-review-2026-11-13) lists what changed and the four decisions agreed that day. Its descriptions of the then-current code are historical. TASK-05A owns the shared JSON/local-output contract, item storage, coordinator, aggregate publication, and common read surfaces. This subtask owns their call integration, including target-version pinning, invocation/message ownership, mapping validation, cancellation, authoring, and call-specific UI and tests.

## Scope and acceptance criteria

- [ ] `callActivity.multiInstance` supports instance-scoped or readable shared typed collection sources in `parallel` and `sequential` modes. Initialization freezes the collection; invalid/oversized input fails before any child is created, and an empty collection produces `[]` without an invocation or runtime target pin.
- [ ] Input mappings accept exact `mi.item`, zero-based `mi.index`, and `mi.total`, frozen parent-instance inputs, and readable parent shared aliases captured when each item first stages, before its child exists. Output mappings target declared locals or authorized `readWrite` shared aliases; shared aggregate/error targets follow TASK-05A. Ordinary call mappings remain compatible.
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

### Foundation already in place (verified 2026-11-13)

TASK-05A, TASK-05B, and TASK-05C built the following. This subtask uses it and does not build it again:

| Area | Built | Left for this subtask |
| --- | --- | --- |
| Release boundary | Publication, runtime entry, and item processing read `AutomaticMultiInstanceRules.InstalledAdapterKinds`, which holds `serviceTask` and `scriptTask`, and all three also require the open Development switch. | Add `callActivity` to the list in the change that completes the call paths. `EnterCallActivityAsync` only calls the runtime guard and then enters an ordinary call, so it needs its body branch before the kind is listed. |
| Authoring rules | `AutomaticMultiInstanceRules.FindLocalProblem` rejects `asyncBefore`, `asyncAfter`, and `job` on a call body and requires item targets to be locals or `readWrite` shared aliases. `CallActivityRules` accepts shared input sources and local or `readWrite` shared output targets on an automatic body. The validator accepts a call body as a timer or conditional boundary host. | `mi.item`, `mi.index`, and `mi.total` as input sources: `NameProblem` still rejects them as reserved names and `InputSourceProblem` as undeclared. Target compatibility for those sources, and the editor mirrors. |
| Schema | Pin columns with `CK_multi_instance_executions_call_pin_shape`; the item key on `call_activity_executions` with its unique filtered index and `CK_call_activity_executions_automatic_item_pair`; ordinary activation uniqueness filtered to rows without an item; unpublish and delete guards for a pinned version in `WorkflowDefinitionRepository`. | Only the `multiInstanceStart` job shape check that TASK-05A left open (§4). |
| Body entry | `InitializeBodyAsync` and `WaitForEntrySlotAsync` accept a `multiInstanceStart` entry job, and that kind already has the control queue class and default retry delays. Initialization always queues a dispatch job and never admits inline. | The entry itself: `ProcessAutomaticBodyEntryAsync` locks the instance first and `HandleAutomaticEntryFailureAsync` clears an `asyncBefore` wait, so neither serves a call body unchanged (§3, §6). The pin is passed in before the collection is counted (§3). |
| Admission | `DispatchAsync` handles the body's counts, the reserved slot, queue-capacity parking, and the nothing-in-flight incident. | `DispatchAsync` and `AdmitJobsAsync` throw for a call body, and the control-job processor builds a `multiInstanceItem` template. Call admission is new (§5). |
| Item fence | `AutomaticMultiInstanceItemFence` and `RequireCallItemFenceAsync` give staging to a `callStart` that owns a queued invocation with no child, and candidate save and publication to the `callResume` of a running invocation with a saved result. `CaptureItemContextAsync` gives a call item the fixed actor and no job or claim context. `SaveCandidateAsync` refuses an item with no staged input. The `StageItem` access phase already reads a call's shared input sources and fences its shared output targets. | The adapter that drives these, settles the invocation, and acknowledges the child's message. |
| Incidents and cancellation | An item counts as blocked while its `callStart` has an open incident. `RetryIncidentAsync` treats a `callResume` whose item has a saved candidate as publication-only. `AbandonAutomaticCallsForTokensAsync` and the lifecycle's `AbandonItemCallsAsync` settle a body's invocations, and call messages correlate only through their invocation. Each item publication cancels the body's open dispatch or completion job and resolves its incidents (`SupersedeControlJobsAsync`). | Nothing new for these paths; §5 and §6 are designed around them. |

## Plan

### 1. Integration with the shared core

Use TASK-05A's one parent token, one automatic MI execution, immutable input snapshot, pending/active item rows, local output declarations, persisted slots, ordered aggregate, and reserved dispatch/completion capacity. Items are not sibling graph tokens or user tasks in the parent; their called workflows own normal independent tokens/tasks.

Calls remain intrinsically durable. **Reject authored `asyncBefore`, `asyncAfter`, and `job` settings on MI calls.** Entering the node queues a `multiInstanceStart` job, a kind the foundation already defines; that job initializes the body, rather than starting a child inside the caller's transition. Do not wrap a call item in a generic REST/script `multiInstanceItem` job or add a second retry owner. Item work continues through `callStart`, `callResume`, and `callCancel`.

**Release boundary.** Publication (`WorkflowDefinitionReadinessChecker.EnsureDurablePublicationAllowed`), runtime entry (`EnsureAutomaticMultiInstanceRuntimeAvailable`), and item processing all read `AutomaticMultiInstanceRules.InstalledAdapterKinds` and also require the open release switch, as [TASK-05A §8](task-05a-multi-instance-core.md#8-definition-validation-compatibility-and-safety-of-loading) records under **Opening by activity kind**. The list holds `serviceTask` and `scriptTask`, so a call body is refused everywhere today:

- Add `callActivity` to the list only in the change that completes intrinsic entry, pinning, admission, delivery, failure, and cancellation. A call body then publishes and runs only on a Development host with the switch open.
- Give `EnterCallActivityAsync` its body branch before the kind is listed. Today a call body that passed the guard would enter as one ordinary call, which the foundation forbids.
- Call entry uses `multiInstanceStart` and call messages, so it does not share the service/script entry and item routing in `WorkflowEngineService.AutomaticMultiInstanceItems.cs`.
- Replace the tests that pin call bodies as refused with tests that a host outside Development refuses them at publication, at entry, and in `callStart`, including a body another host published, whatever its options say.

Each body has exactly one unconditional ordinary outgoing flow. It has no selectable outcomes, engine-only fallback, completion priorities, or human vote functions. One interrupting catch-all error boundary may attach, plus up to eight timer and conditional boundaries combined, with either `cancelActivity` mode. HTTP status matching remains invalid. Every boundary attaches to the whole body, never one item in isolation. Widen host eligibility for automatic MI calls explicitly in server/editor validation and runtime setup; ordinary single-call restrictions stay unchanged.

Follow TASK-05A's boundary lifetime: conditional subscriptions begin at token entry and can interrupt a queued entry, including a token waiting for another body's node; timer subscriptions begin once at successful body initialization. Keep them through item work, capacity/incident waits and completion publication. Close them when the body settles or an error boundary wins. No authored async flags are needed to make an MI call an eligible host, and retries never rearm a consumed timer. A condition observes the parent instance, not a child's variables or shared-key changes.

Count body entry once under the parent's automatic-activation loop guard. Fan-out uses the MI instance limit, not one loop-guard charge per item. Preserve the guard within each called workflow; retries or aggregate delivery must not reset it and permit an infinite parent loop.

The shared core freezes the collection and parent-instance inputs at successful durable initialization. A mapping that names the collection alias, or another alias resolving to that same shared key, always reads the body's frozen collection. Capture other readable parent shared aliases once per item when it first stages: the first, parent-only transaction of its `callStart`, under the parent's `StageItem` access phase (§5). The item row keeps the staged values, binding identity and output revisions, and the invocation keeps the mapped inputs. Items that stage later may observe newer shared values, while retries of a staged item retain its saved inputs. Calls use the fixed `callActivity` actor with no inherited roles, JWT claims, or delegation. Map requester/approver information as explicit data when needed. Keep call mappings narrower than REST/script contexts: shared aliases are supported, but config, setting and arbitrary expression sources remain invalid.

### 2. Workflow JSON and mapping rules

Use the shared `MultiInstanceModel` additions: collection-only source, `afterAll` completion, optional `maxConcurrency`, declared `outputVariables`, and an instance-scoped or authorized shared scalar JSON `resultVariable` initialized to `[]`. TASK-05A owns exact common defaults, limits, result envelope, shared access/`ValueRevision` fences, name and resolved-key collisions, and validation order.

Keep `CallVariableMappingModel`'s strict shape, whole-variable copies, 100-mapping limit, and missing/null/required distinctions. Add no implicit conversion, expression, JSON path, or mapping-specific default:

| Mapping part | MI-call rule |
| --- | --- |
| Input `sourceVariable` | Only here, permit exact `mi.item`, `mi.index`, and `mi.total` as narrow reserved-prefix exceptions. Other names read the frozen parent-instance snapshot or a declared readable parent shared alias captured when the item first stages. The collection source and aliases resolving to its key always use the frozen body collection. Once the stage is saved, no `callStart` attempt resolves an alias again against live shared values. |
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

For each nonempty body, resolve the target family's published default and default plain start **once during successful durable initialization**. Acquire the target-family lock before parent-instance/runtime locks. The body row already has `PinnedChildDefinitionId`, `PinnedChildStartEventId`, and `PinState` (`pinned`, then `released` at settlement). Failed initialization may retry resolution only until that pin commits. Empty input creates no invocation and needs no runtime target pin; definition publication still validates its target.

The family lock comes before the instance lock, so the target is resolved before the collection is counted, while the coordinator counts the collection inside `InitializeBodyAsync`. The repository refuses a pin on an empty call body and a nonempty call body without one, so the entry cannot decide alone. Change the seam as follows:

- The `multiInstanceStart` job locks the family, reads the published default, and runs `CallTargetReadiness.FindCallTargetProblems` against it. It passes the coordinator either a pin or the target failure (`call_target_unavailable` or `call_target_incompatible`).
- The coordinator stores the pin only when the frozen collection is nonempty. For an empty collection it drops the pin and ignores a target failure, so an empty body completes with `[]` even when its target is missing.
- For a nonempty collection without a pin, initialization fails with the target failure before any body or item row exists. §6 gives its exit.
- Initialization also checks the call depth once (`call_depth_exceeded`). Every item of a body starts at the same depth, so a per-item check could only fail every item.

Both definitions are immutable while the body is open, because the parent cannot switch version and the pin never changes. The contract check therefore runs once, at initialization, and is not repeated per item.

`StartInstanceCoreAsync` already accepts an exact definition id. Route item starts through it with the pinned definition and start event instead of the family key, and confirm it starts a published version that is no longer the default. Do not add another public/authored version binding, or re-resolve the default per item. **Every item `callStart`, including retries with an existing pin, acquires the target-family lock before parent runtime locks**, as `ProcessCallStartJobAsync` does today, because internal child creation also uses the family lock. Start legality, mapped input values, and business-key uniqueness are rechecked for each child.

A pinned version can stop being startable while its body runs. When a newer version of the family enables business keys, the engine refuses to start an older unkeyed version, so every remaining item fails with `call_start_failed` and no retry can succeed. **Decided (decision 3):** add no new guard. Each such item takes the catch-all error boundary, or waits in its `callStart` incident until the body is cancelled or interrupted, which is the known stuck-body limit of the first release. Document it with the pin, and pin it with a test.

The deletion and unpublication guards for a pinned target are built: `WorkflowDefinitionRepository` refuses to unpublish a version that an active call body pins or that open item work still needs, and deletion counts any body that names the version. They cover the interval before the first child exists. This subtask adds the tests and the Operations wording (§7). Changing the family's default remains allowed and affects new bodies only. Preserve ordinary invocations' existing resolution timing, deletion guards, and caller compatibility previews. Document that an operator cannot retire a pinned version until the bodies that pin it settle or are cancelled.

The core blocks parent version switches while automatic entry/body/completion work is open. Retain existing unsettled-call blockers on both parent and child as well; include pending and queued items, incidents, and result delivery. Settled history alone does not block a later compatible switch. Do not partially rebind active item jobs or their pinned definitions.

Automatic bodies/items are not reactivation targets. Keep ordinary-user-task target rules, prohibition on directly reactivating linked children, and the requirement that calls settle and descendants stop before root reactivation. A later valid revisit creates a new body/item/invocation identity and takes a new pin; it never replays a settled child.

### 4. Invocation ownership, persistence, and migration

The foundation's three migrations already hold the call-specific schema; do not create competing body/item/coordinator migrations. All objects live in `flowbit`. The table states the design and what is left:

| Storage concern | Required design | State |
| --- | --- | --- |
| Body pin | Target definition/start and pin lifecycle on the core's parent execution. An invocation's populated-child identity columns never hold a pre-creation body pin. | Built, with `CK_multi_instance_executions_call_pin_shape`. The coordinator seam changes in §3 need no schema change. |
| Invocation item ownership | A nullable automatic-item FK on parent-owned `call_activity_executions`, unique for non-null items, with distinct invocation activation/fence and separate body activation. One item owns at most one invocation and one child. | Built, with `CK_call_activity_executions_automatic_item_pair`. |
| Ordinary activation uniqueness | Ordinary `(parent instance, token, activation)` uniqueness filtered to rows without an item, plus automatic-item uniqueness. Existing ordinary rows retain null item correlation. | Built. |
| `multiInstanceStart` job shape | A check constraint for the entry kind: no body, item, or control generation, an instance and token, and the control queue class. Add it `NOT VALID` and validate it in a separate migration that runs outside a transaction, as the foundation's widened checks do. | **To add.** TASK-05A left it open. |
| Cross-instance messages/results | Keep child-owned result and `callResume` rows free of parent-item FKs. Resolve the item through its parent-owned invocation at delivery. Preserve existing invocation correlation and cross-instance message ownership. | Built; `CK_workflow_jobs_automatic_item_shape` refuses an item id on any call message. |
| Shared input/output state | The item row keeps the staged shared inputs, resolved bindings and output `ValueRevision` fences, saved by the first `callStart` transaction, and later the validated local/shared publication candidate. The invocation keeps the mapped inputs and the child's result row the saved outputs. A duplicate resume cannot apply the same shared writes twice. No child-owned FKs to parent shared/item state. | Columns built; the adapter writes them (§5). |
| Boundary ownership | Reuse timer/conditional subscriptions and occurrences keyed by parent token activation, never one set per child. Cancellation can find entry work before a body exists and all automatic items after initialization; retain pending occurrence fences according to existing event retention rules. | Built in the core, which already recognizes a `multiInstanceStart` entry wait as a host; verify with call bodies. |
| Ledger | Use the core's `automaticActivityItem` visit and item correlation. Create pending ledger rows in bulk; no duplicate body node visit, parent `user_tasks`, role policies, or vote-count rows. Called workflows keep their own normal ledger. | Built in the core. |
| Read indexes | Support body/item lookup, invocation lookup, active pin guards, bounded call paging, and grouped counts without reading snapshot payloads. | Check the calls page and the pin guards against the existing indexes; add one only where a query plan needs it. |

A compact item identity must survive for the invocation's lifetime. Payload/history pruning must never null the item FK and make an MI invocation look like an ordinary call under the filtered uniqueness rule. Preserve settled invocation identity/fences under existing call retention rules.

Protect pending/active/incident-blocked body pins, item results, invocations, child result snapshots, jobs, and completion records until their required delivery/publication is durable. Existing protection through token `WaitingJobId` is insufficient for many items, and completed jobs are pruned by age while the parent still runs, so follow the [core's retention rules](task-05a-multi-instance-core.md#5-persistence-and-migration): the body row references its snapshot, and items keep their own results. Define FK delete order and retention eligibility after a child succeeds while its parent still waits. Mark `HistoryPrunedAt` where existing pruning rules require it, without fabricating historical automatic executions or silently discarding recovery state.

Apply existing 1 MiB per-call input/output snapshot limits alongside the [core's body limits](task-05a-multi-instance-core.md#4-limits-and-admission), including its result-size refusal at entry. The two limits use different measures. A call snapshot counts compact JSON (`CaptureMappedValues`). An item's staged input plus candidate is limited to 1 MiB of the text PostgreSQL prints for `jsonb` (`JsonbText.ByteCount`), and the body's result has its own 1 MiB limit. A child output can therefore pass the call limit and still not fit the item or the result; §6 gives that case its own exit. Never measure compact JSON for a limit a database check also enforces.

Do not copy the full collection into every invocation snapshot. The one deliberate exception is a mapping that names the collection alias: it passes the whole frozen collection to each child, within the per-call input limit, and field help says so.

Only the `multiInstanceStart` shape check changes the schema. Update `Data/AppDbContext.cs`, its migration/designer/model snapshot, and the schema documentation together, and return the change to TASK-05A's migration sequence. Tests must preserve populated ordinary calls, pending call messages, human MI, and normal jobs, and prove the existing constraints reject swapped item ownership or duplicate children.

### 5. Admission, messages, and transaction protocol

Admission creates the parent-owned invocation with immutable item/body correlation and one stable invocation identity. Pending unadmitted items are descriptors, not child instances or call reservations. Use ascending item index and TASK-05A's persisted concurrency window; no in-memory semaphore or Worker lease is the authority.

Admission runs only in the body's `multiInstanceDispatch` job. Initialization and every item publication queue that job; neither admits inline, so admission never adds locks to a `callResume` that already holds its publication keys. Add a call admission operation to the coordinator beside `AdmitJobsAsync`, and a call branch to the control-job processor, which today builds a `multiInstanceItem` template. For each item in the available window it marks the item active, takes the root reservation, creates the invocation with no inputs, and queues the item's `callStart`. It reads and locks no shared row.

**Decided (decision 1): an item stages in its `callStart`, not at admission.** The earlier text captured shared inputs at admission, before an invocation existed, and put a failed capture on the entry or dispatch job with item correlation. The foundation cannot hold that state:

- staging needs a queued invocation and a live `callStart` lease;
- a control job cannot name an item;
- item blocking is read from an open incident on the item's `callStart`;
- every item publication cancels the open dispatch job and resolves its incidents, so an admission incident would close and retry itself whenever a sibling settled.

Instead, each `callStart` attempt runs up to two transactions:

1. **Stage (parent only).** Under the parent's runtime locks and the `StageItem` shared keys, `CaptureItemContextAsync` saves the item's shared inputs and output revisions on the item row, and the adapter maps `mi.*`, the frozen instance inputs, and those shared values into the invocation's inputs. It commits and discards tracked state. An item with a saved stage skips this transaction.
2. **Start.** The family lock, then the parent's runtime locks and the item/invocation fence, then the child from the pin and the saved inputs. No parent shared row is read or locked.

A failed stage commits nothing, so no failed capture becomes a frozen input. Its retry captures the shared values current then, and once a stage commits every later attempt reuses it. A missing, archived, or contract-changed binding, denied access, or a statically read alias with no value is an operational `callStart` incident and takes no business error boundary, as [service adapter decision 4](task-05a-multi-instance-core.md#8-definition-validation-compatibility-and-safety-of-loading) settled for REST items. A value that violates an otherwise valid input contract (`call_input_invalid`) takes the whole-body catch-all boundary, or opens a `callStart` incident when none is attached. While either incident is open the item is active, holds its slot and its queued invocation, and counts as blocked, so the core pauses new admission while admitted work settles. Failures before body/item creation remain on the entry job without fabricated item or child records.

An admitted call keeps its MI slot while the child waits for human work, messages, timers, conditions, nested calls, or an internal incident, and until the local result and any shared write candidate commit together. No Worker lease remains held through that wait. Sequential mode starts item N+1 only after N settles successfully. The core pauses new admission on a body item-level start/output incident; already admitted work may settle. A child's internal incident only occupies its slot and does not pause other available parent slots.

Enforce `WorkflowCallActivities.MaxDepth` (current default 8) and `MaxOpenInvocationsPerRoot` (current default 256) in addition to the MI instance/concurrency settings and queue limits. The root limit remains an existing **soft admission threshold** across concurrent parents. Do not add a root-instance lock or claim a hard global child maximum. Retries retain/reuse existing reservations; rejected calls consume no reservation. Nested calls remain subject to normal depth/admission and child-loop guards.

Reaching the root limit is back-pressure for MI items, not a failure. An ordinary call refused there takes its catch-all boundary or opens an incident, but an MI body's own window can exceed the soft limit, so an MI item refused at admission stays pending: it creates no invocation, consumes no reservation or attempt, and takes no boundary. Progress reports admission as blocked by call capacity (`callCapacity`, which the instance page already words), and the reserved dispatch job parks and retries with the core's bounded backoff, as it does for queue capacity. When the body has no admitted, unsettled item **and** that backoff has reached its maximum delay, nothing of its own will free capacity, so the dispatch job opens an incident with the existing `call_capacity_exceeded` code instead of backing off silently. Both conditions are required, as in the core's queue-capacity rule: a sequential body has nothing in flight between any two items, so an incident on the first refusal would stop it whenever the root was briefly full. The incident makes nested stalls visible, and retrying it repeats admission. A depth refusal cannot change by waiting; initialization checks it once (§3).

A body can also starve its own children. Its window can reach 1,000 items while `MaxOpenInvocationsPerRoot` defaults to 256, and its reservations count against the same root as every call its children make. Once the body holds the root's capacity, an ordinary call inside a child is refused as a failure, not as back-pressure, and its incident cannot be retried successfully until the body's other children finish. **Decided (decision 2):** keep ordinary-call semantics unchanged in this release. Document that a call body whose children make calls needs `maxConcurrency`, multiplied by the calls each child holds open, to fit the root limit; say so in the field help; and pin the behavior with a test. Making nested ordinary calls wait instead of fail changes TASK-01's contract and is left to a later release.

Queue-capacity contention is distinct from call admission failure. Use TASK-05A's reserved, deduplicated dispatch/completion control work for temporary job capacity; do not consume call/activity attempts, lose pending wake-ups, or roll back committed successful item results, and open a contention incident only under the core's rule for a body with nothing in flight. Call admission failures other than the root limit retain their own incident/boundary semantics.

| Phase | Call-specific responsibility |
| --- | --- |
| Enter/initialize | Attach/evaluate conditional subscriptions and queue intrinsic entry if still eligible; wait without consuming attempts while another body of the node is open. Lock target family before parent runtime state, resolve the optional pin and check depth (§3), snapshot, install core body/items/ledger and timer subscriptions, reset aggregate, drain notifications/recheck fences, and queue dispatch. Empty input queues aggregate completion without calls. |
| Admit item (dispatch job) | Under parent runtime locks only, in ascending index within the window: check root capacity, mark the item active, create its reserved invocation without inputs, and queue its `callStart`. A capacity-refused item stays pending with no slot or invocation. No shared row is read or locked. |
| `callStart`: stage | Parent only. Fence activation/body/item/invocation and the message lease, lock the `StageItem` shared keys, save shared inputs and output revisions on the item and the mapped inputs on the invocation, then commit. Skipped when the stage is already saved. |
| `callStart`: start | Lock family first on every attempt, fence again from fresh state, and create at most one child from the pin through the exact-definition start, using the saved inputs. Do not read or lock parent shared rows. No second generic item job. |
| Child terminal commit | Capture mapped child outputs and enqueue existing result/message without locking or writing parent runtime/item rows. Preserve completed child state if mapping later fails. |
| `callResume`: save candidate | Lock the receiving parent only, fence invocation/item/body, map the saved child output into local and shared candidates in memory, and save the candidate on the item. Commit before any publication, so a conflict cannot roll back the only accepted result. Skipped when a candidate is already saved. |
| `callResume`: publish | Lock the receiving parent and its ordered `PublishItem` keys, check output revisions, and commit shared writes, locals, item settlement, invocation settlement and the message acknowledgement once. The core queues the next dispatch or the completion job; this transaction admits nothing. No direct per-item parent instance write or normal outgoing traversal. |
| Aggregate | Core completion publishes ordered saved locals to the instance/shared aggregate target, drains parent conditional notifications, and continues once as `callActivity` if still eligible. A delivery or aggregate retry never recreates a successful child. |
| `callCancel` | Parent settles invocation/cancel-message state first; each message then locks only its receiving child and propagates cancellation one level at a time. |

Preserve the **one existing instance per transaction** rule. Parent cancellation cannot wait on a child's runtime row; child completion cannot acquire the parent's row. Add the automatic-item FK only to parent-owned invocation state; avoid new cross-instance FKs that introduce hidden lock acquisition.

Within parent mutation, follow the core's instance → gateway execution/state/branch → parent token → MI execution → items in ID order, then the existing job/incident protocol and its shared-access ordering. Acquire the complete shared key set in the core's deterministic order, and include synchronous conditional continuations in the readiness proof; a late discovery of an earlier key is not allowed. The target family precedes the runtime sequence for initialization and every item start. The stage transaction of `callStart` is parent-only and commits before the start transaction, so no transaction holds the family lock and a parent shared row together, and shared access cannot add a hidden cross-instance lock. Audit lease, retry, heartbeat, cancellation, retention, and definition guards for inverse locks. Use fresh database state after staging; a token ID alone is not a sufficient fence.

Fence immutable definition, parent/body activation, execution, item, invocation and relevant job/lease generation, plus eligible states. Duplicate result delivery, immediately available child results, simultaneous last-item resumes, and retry after cancellation cannot increment counts twice or queue a second completion.

### 6. Failure, cancellation, recovery, and audit

| Call outcome | Required MI behavior |
| --- | --- |
| Entry failure before a body exists: invalid or oversized source, a result that cannot fit, an unavailable or incompatible target, or the depth limit | **Decided (decision 4):** the exit REST and script entries already use. The catch-all error boundary takes it at once with the stable code and no partial result, through `EnterAutomaticErrorBoundaryAsync`. Without a boundary the `multiInstanceStart` job retries on its default delays and then opens an incident. A retry repeats initialization from the start, resolving the target again. Generalize `HandleAutomaticEntryFailureAsync`, which clears an `asyncBefore` wait, to the `multiInstanceStart` wait. No item, invocation, or child is created. |
| Shared binding/access/lifecycle failure while an item stages | Open an operational incident on the item's `callStart`. The item stays active and blocked with its slot and queued invocation, but with no saved stage and no child; the core pauses new admission. Do not route a business error boundary. Retry after the cause is repaired; the stage captures the shared values current at its first success. |
| Input value rejected by a valid contract while an item stages | `call_input_invalid`: take the whole-body catch-all error boundary if present; otherwise open the `callStart` incident above. The retry stages again. |
| Start or business-key failure after the stage is saved | Take the whole-body catch-all error boundary if present; otherwise wait with a retryable `callStart` incident. Retry the same invocation with its saved inputs and the committed pin. A pinned version that can no longer start fails every retry (§3). |
| Root open-call limit reached at admission | Back-pressure, not a failure: the item stays pending, takes no boundary, and the dispatch job retries. A body with nothing in flight whose backoff is at its maximum opens a `call_capacity_exceeded` incident (see §5). |
| Child internal job incident | Child and call item wait. Recover that child's job under its own authorization; do not treat it as termination or free the slot. |
| Successful child, including its own terminate end | Deliver mapped locals and authorized shared writes atomically with item success; the core then queues refill or aggregation if still eligible. Termination affects only that child. |
| Child fault or independent cancellation | Take the body's error boundary and cancel unfinished siblings; without an error boundary, fault the parent through its normal terminal-status path and cancel unfinished work. Never automatically replace the child. |
| Invalid child output/local result | `call_output_invalid`: take the error boundary if present; otherwise open an output-delivery incident on the `callResume`. The child remains completed and is never run again. A local validation that fails only at publication is the same failure of the saved candidate, as [script adapter decision 2](task-05a-multi-instance-core.md#8-definition-validation-compatibility-and-safety-of-loading) settled. The child's output and the item's frozen inputs cannot change, so without a boundary a retry succeeds only when a shared value the validation reads has changed; otherwise this is the stuck-body limit. |
| Child output that cannot fit the item or the body result | Not an incident, because no retry can change it. `RefuseItemResultAsync` stops the body at once and publishes nothing; completion then takes the catch-all error boundary or faults the instance with `multiInstanceFailed`. The child remains completed. The adapter settles its invocation as failed and acknowledges the message in the refusing transaction, so stopping the body sends no cancel message to a finished child. |
| Shared output-version conflict | Open an operational delivery incident, preserving saved child output and the complete publication candidate; a business error boundary does not catch this conflict. Authorized recovery rechecks binding/access/validation and explicitly accepts publishing that saved candidate over the newer value, then refreshes the output fence under the core contract. It never recomputes/merges the candidate, silently last-write-wins, duplicates a committed write, or recreates the child. |
| Aggregate output-version conflict | Retry the core completion incident from saved item locals, without starting or reinvoking children. |
| Aggregate validation/size failure that retry cannot change | Take the error boundary or fault the instance under TASK-05A; never create an endlessly retryable completion incident. |
| Interrupting timer/conditional capture | Stop the whole body's unfinished work, close subscriptions/recovery, durably cancel descendants and route the captured boundary once. Do not publish an error-style partial aggregate. Before initialization, cancel only this token's entry work; another token's active body is unaffected. |
| Non-interrupting timer/conditional capture | Create the normal sibling handler token and leave body/items/children running. Recurring timers and false-to-true conditional rearming retain the existing occurrence rules. |

Calls do not acquire REST/script `boundaryFirst`/`retryFirst` policies. Keep existing bounded, value-free `call_*` diagnostics, scalar error-boundary output and normal Operations retry authorization; an MI error target may be an authorized shared alias under the core's write/fence contract. Parent call continuation uses the fixed `callActivity` actor, never a child approver or retrying operator's roles/claims/delegation. Timer/conditional handlers retain their normal event attribution rather than fabricating a child action.

For an admitted item, a recoverable stage/start/output incident leaves its ledger visit active, with failure evidence in attempts/incidents; retry restores work state without reopening a terminal ledger visit. An item the root limit holds back was never admitted, so it and its ledger visit stay pending. An error boundary or terminal failure faults the relevant parent-item visit; timer/conditional interruption and other cancellation cancel unfinished visits while previously successful items remain completed. An invalid mapped result never changes a completed child's own status/ledger. Preserve failed sibling evidence when closing superseded recovery paths.

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

Close the call-related editor findings [TASK-05A left open](task-05a-multi-instance-core.md#open-findings-corrections--2026-11-11): disabling multi-instance or changing type does not review call-only mappings; **Remove these options** on a call also removes a valid automatic body; and a falsy imported `multiInstance` loads as an enabled one. The mapping selectors must offer `mi.item`, `mi.index`, and `mi.total` only on a call body, and `callSourceDeclarations` stays inside the save-validator markers.

Render sequential/parallel MI markers while retaining the call marker. Check accessible labels/help, standalone/hosted import/export, copy/paste, type conversion, and read-only diagrams without adding a runtime dependency or duplicate editor implementation.

Extend `InstanceCallActivities.razor` to distinguish same-node invocations by body/item, show the common pinned version, and preserve paged child/parent navigation and descendant-cancellation notices. Use the core paged automatic result presentation rather than human User/Action/Submitted-data columns. Show empty completion, waiting child work, shared-publication conflicts, timer/conditional handlers, incident blocking, partial cancellation, and loading/error states. Clear protected details on identity changes, discard stale page responses, and keep links permission-aware; real tasks inside the child remain governed by its own UI/authorization.

### 8. Implementation sequence and ownership

Paths below are relative to the repository root. The proposed shared coordinator belongs to TASK-05A; integrate through its ports rather than duplicating body lifecycle code.

| Step | Principal files/work | Gate |
| --- | --- | --- |
| 1. Contract characterization | `Flowbit/src/Flowbit.Shared/Models/CallActivityRules.cs` (item sources in `NameProblem`, `InputSourceProblem`, and `FindTargetProblems`), `AutomaticMultiInstanceRules.cs`; validator/readiness and editor mirrors. The shared-access phases and boundary-host rules exist; pin them with call cases. | Ordinary calls/human MI unchanged; item/local/shared mappings, MI-only timer/conditional attachments, forbidden flags and target validation agreed with core. |
| 2. Persistence | The `multiInstanceStart` shape check in `Data/AppDbContext.cs` with its two migrations; tests for the existing pin, item-ownership, and uniqueness constraints and the definition guards; retention. | Fresh/populated migration, item uniqueness, pin protection, pending-message compatibility, no cross-instance lock regression. |
| 3. Initialization/start | A new `WorkflowEngineService.AutomaticMultiInstanceCalls.cs` for the body branch of `EnterCallActivityAsync`, the `multiInstanceStart` processor with the optional pin, and the two-transaction item `callStart`; call admission in `AutomaticMultiInstanceCoordinator.Calls.cs`; the call branch of the control-job processor in `WorkflowEngineService.AutomaticMultiInstance.cs`; the pin seam in `InitializeBodyAsync` and `AutomaticMultiInstanceRepository`; the exact-definition start. | One committed body pin and one child per item; family-first locking; parent-only shared input/output-fence staging; boundary activation; parallel/sequential waits and admission limits. |
| 4. Delivery/cancellation | The item branch of `callResume` (save candidate, then publish, refuse, or capture a failure), existing call cancel, timer/conditional capture, coordinator notifications, ledger, version/reactivation/terminal gates. Add `callActivity` to `InstalledAdapterKinds` here, when every path is complete. | Duplicate/late results, mapped-output and shared-publication failure, boundary/parent failure, ancestry fences and saved-result retry without replacement children. |
| 5. Reads/API/UI | `WorkflowInstanceProjectionService.cs`, call DTOs and instance endpoints, item/node/job projections, typed UI client, `InstanceCallActivities.razor`, canonical editor assets. | Bounded queries, permission matrix, authoring parity, navigation and real browser behavior. |
| 6. Documentation/release evidence | Guides and paired examples below, regression suites, browser acceptance and rollout rehearsal. | All call criteria and shared-core integration gates pass; no first-release deferral of calls. |

Call mapping/UI preparation can run after the shared contract stabilizes, alongside core persistence work. Do not ship call initialization independently of cancellation, delivery, retention, pin guards, and recovery.

### 9. Automated and browser verification

Extend [call definition tests](../../Flowbit/tests/Flowbit.Tests/CallActivityDefinitionTests.cs), [editor call tests](../../Flowbit/tests/Flowbit.Tests/EditorCallActivityTests.cs), and existing lifecycle/admission/failure/cancellation/recovery/operations/persistence suites under `Flowbit/tests/Flowbit.Tests`. Add focused MI cases, preserving ordinary call and human MI regressions:

| Group | Required cases |
| --- | --- |
| Model/mappings | Instance/shared typed collections; item/index/total and shared inputs; missing/null/required/default/type/array rules; empty locals; local/shared output selection; readable versus readWrite bindings; source/aggregate/error key overlap across aliases; ordinary reserved names and ordinary-call restrictions; unsupported flags/per-item boundaries; wrong imported values and ordered editor errors. |
| Release boundary | A host outside Development refuses a call body at publication, at entry, and in `callStart`, including one another host published; a call body never enters as one ordinary call; the kinds stay refused until listed. |
| Pinning | One pin per nonempty body; default changes between sequential admissions and retries; pre-pin failure then retry; an empty body with a missing target completes with `[]` and no pin; a nonempty body without a target fails at entry; the depth limit fails at entry before any item; a pinned version that a newer keyed version makes unstartable; a pinned non-default version still starts; pinned target deletion/unpublication before first child and during recovery; normal call resolution unchanged. |
| Lifecycle/slots | Both modes; immediate completion; human/message/timer/conditional/nested waits; out-of-order ordered results; no held lease while waiting; business-key limits; root-limit back-pressure, including a sequential body that waits through a brief full root without an incident, the nothing-in-flight incident at the maximum backoff, a nested stall, and children whose own calls are refused while the body holds the root; rejected/retried reservations; queue contention and control wake-up recovery; admission only through the dispatch job. |
| Results/retry | One child per item across duplicate starts/restarts; duplicate/simultaneous last resumes; invalid output preserves completed child; result-size limits, including an output within the call limit that the item or body result refuses, with the child's invocation settled and no cancel message; local/shared output atomicity; conflicting same-key item writes, other-instance writes and archived bindings; saved-candidate authorized retry without child replay, recomputation or duplicate shared writes; shared aggregate/error target fences. |
| Shared snapshots/locks | Frozen shared collection, including another alias for its resolved key, versus other shared inputs captured when each item stages; updates between sequential items and retries; no transaction holding the family lock with a parent shared row; validation dependencies; no shared locks through child creation/child lifetime; parent-only output delivery and one-existing-instance transaction invariant; readiness rejects an unsafe synchronous boundary continuation. |
| Failure semantics | Entry failures taking the catch-all at once or retrying into a `multiInstanceStart` incident, with no body, item, or child; shared binding/access failure at staging versus a value rejected by a valid input contract; the `callStart` incident blocks its item and pauses admission while siblings settle, and a sibling's publication does not close it; a failed stage saves nothing and its retry captures fresh values until the first success; later retries keep frozen inputs; child fault/cancel versus internal incident; no-error-boundary fault versus operational incident; error boundary wins once and publishes the partial result; shared conflicts bypass business boundaries; failed sibling evidence; fixed actor/claim isolation; no REST retry policy applied to child workflows. |
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
7. Change a shared input between sequential admissions and verify the next item sees the new value while a staged item's retry keeps its snapshot. Cause a real shared-output conflict, inspect authorized recovery and its saved-candidate overwrite decision, and prove no child restarts or shared write repeats.
8. Trigger an interrupting timer while children wait and a conditional while entry is queued; confirm immediate descendant fencing, asynchronous cancellation, no new partial aggregate and no effect on another token's body. Exercise non-interrupting reminders/false-to-true handlers and final-publication races with real writes and Worker responses.

Capture editor controls/markers and queued, running, blocked, completed, and cancelled runtime screens. Inspect browser errors/warnings and host logs separately. Record exact commands/results, browser/version, localhost URLs, interactions, viewport sizes, screenshots, console results, and unavailable checks. Jint/unit/DOM-stub tests do not satisfy real-browser verification.

### 10. Documentation, examples, rollout, and handoff

Follow the timing in [TASK-05A §13](task-05a-multi-instance-core.md#13-documentation-and-completion-handoff), as TASK-05B and TASK-05C do:

- **With this subtask's code:** implementation invariants in [AGENTS.md](../../AGENTS.md) and the [runtime reference](../../Flowbit/README.md), and any schema, route, DTO, or operational reference this subtask changes, marked inactive until automatic multi-instance is enabled.
- **Prepared here, merged in the parent's integration change:** the call sections of the behavior guides in the table below, the canonical examples, and the call rules for the AI authoring guide. The AI authoring package bundles those guides and the example catalog, so merging them earlier would tell the assistant to author definitions that publication still refuses. Until then keep the example definitions as test fixtures outside `examples/` and run them over HTTP on the Development test hosts.

| Owning documentation | Call-specific update |
| --- | --- |
| [AI authoring guide](../../authoring/flowbit-authoring/references/authoring-guide.md) and `SKILL.md` | In the integration change: replace the statement that a call cannot use `multiInstance` or timer and conditional boundaries with the call-body rules, including item sources, local and shared targets, the single pin, and the forbidden async and job settings. |
| [BPMN support](../bpmn-support.md), [node reference](../node-reference.md) | First-release MI calls, instance/shared input and local/shared output contract, single pin, intrinsic durability, MI-only timer/conditional hosts and lifetime, error limitations, limits and deferred scope. |
| [Developer guide](../developer-guide.md) | HTTP-only paired parent/child walkthroughs, collection versus item shared snapshots, output aggregation/shared conflict recovery, fixed identity, failure taxonomy, boundary interruption/reminders, cancellation, incidents and pinning across default changes. |
| [API reference](../api-guide.md), [UI guide](../ui-guide.md) | Call/item correlations, paging/auth/errors, relation navigation, authoring, recovery, pin/version blockers, and changed screenshots. |
| [Deployment](../deployment.md), [database schema](../database-schema.md) | Call admission interaction, including root-limit back-pressure and its incident, migration objects/constraints/indexes, pin protection, one-instance lock/message ownership, retention/recovery and ordered upgrade. |
| [Runtime reference](../../Flowbit/README.md), [AGENTS.md](../../AGENTS.md) | Call coordinator integration, item/invocation/body and boundary fences, family-before-parent locks, parent-only shared staging/publication, saved-result recovery, ledger and no cross-instance item FK invariant. |
| [Example catalog](../../examples/README.md), [MI examples](../../examples/multi-instance/README.md), [call examples](../../examples/call-activities/README.md) | Paired MI parent/child examples in both modes, shared reads/writes/conflicts, interrupting and non-interrupting timer/conditional boundaries, empty input, child error-boundary/incident recovery, explicit inputs/outputs/target publication/Worker prerequisites. |
| [TASK-01](task-01-call-activity.md), [parent TASK-05](task-05-multi-instance-automatic-activities.md), [backlog](README.md) | Preserve TASK-01's historical decisions, point its MI follow-up to current TASK-05, and synchronize actual implementation/evidence status. |

Update root capability summaries, getting started, documentation home, links, or screenshots only where behavior makes them inaccurate. Validate relative links/anchors/images and parse JSON examples; run canonical examples through HTTP and existing authoring catalog checks. Do not change AI tool names, descriptions, or system instructions for this feature.

Follow the parent's [coordinated rollout and rollback limits](task-05-multi-instance-automatic-activities.md#documentation-and-rollout). The call-specific rehearsal adds pending call messages and active target pins to its populated-database checks.

Handoff to TASK-05A must include the coordinator changes this subtask makes (call admission, the optional pin at initialization, and the entry-failure exit for a `multiInstanceStart` wait), the `multiInstanceStart` shape check and its lock order, call adapter/result/cancellation hooks, failure-to-core-state mapping, read correlations, retained-identity rules, and test evidence for shared dispatch/completion. Mark TASK-05D Done only after its criteria pass with the shared core; mark TASK-05's first release Done only when its service, script, **and call** subtasks are integrated and verified. Record remaining later-release features separately; basic MI calls cannot be deferred to close this release.

## Plan review (2026-11-13)

This plan was reviewed against the parent plan, TASK-05A, the TASK-05B and TASK-05C implementation records, and the code at commit `a4b5523`. No call-adapter code exists yet, and nothing was run for this review. The review changed the plan as follows:

- **Current state** has a new table of what the foundation and the two landed adapters already provide for calls, and what each area leaves for this subtask. The earlier text planned the pin columns, the item ownership key, the filtered uniqueness, and the definition guards again.
- **Release boundary** (§1) names the installed-kind list instead of unconditional guards, and records that `EnterCallActivityAsync` needs its body branch before the kind is listed.
- **Pin seam** (§3) is new. The target is resolved under the family lock before the collection is counted, and the repository refuses a pin on an empty body, so the coordinator takes an optional pin. The contract check runs once, and the depth check moves to initialization. The exact-definition start already exists.
- **Storage** (§4) marks each concern built or to add. The only schema change left is the `multiInstanceStart` shape check. The size paragraph records that call snapshots and item limits use different measures.
- **Admission** (§5) runs only in the dispatch job, because initialization and publication always queue it, and the coordinator needs a call admission operation that does not exist.
- **Capacity incident** (§5) now needs both nothing in flight and the maximum backoff, as the core's queue-capacity rule does. The earlier text opened the incident on the first refusal, which would stop a sequential body whenever the root was briefly full.
- **Failure exits** (§6) add the entry-failure row and the refusal row for an output that cannot fit, and state when an output incident can and cannot be recovered.
- **Editor** (§7) picks up the three call-related findings TASK-05A left open.
- **Tests** (§9) add the release boundary, the pin cases, the capacity cases, the refusal case, and the staging failures.
- **Documentation timing** (§10) follows TASK-05A §13, and the AI authoring guide is listed. The earlier text updated behavior guides and examples when the code landed, while publication still refuses call bodies.

Four decisions change or extend a shared contract. All four were agreed on 2026-11-13 as proposed, and TASK-05A records them under **Call adapter decisions** in its [§8](task-05a-multi-instance-core.md#8-definition-validation-compatibility-and-safety-of-loading). They are decisions for this subtask's implementation, not implemented behavior:

1. **An item stages in its `callStart`, not at admission** (§1, §2, §5, §6). Admission creates the slot, the reserved invocation, and the `callStart` job; the first `callStart` transaction captures shared inputs. A capture failure is a `callStart` incident that blocks its item. The rejected alternative kept capture at admission and changed the foundation instead: an item correlation on control jobs, blocking read from dispatch incidents, and a dispatch incident that a sibling's publication no longer closes. TASK-05A §3, §4, and §6 now use the same wording.
2. **A body that starves its children's own calls is documented, not prevented** (§5). The rejected alternative made a nested ordinary call wait at the root limit instead of failing, which changes TASK-01's contract.
3. **A pinned version that can no longer start adds no guard** (§3). Its items take the catch-all boundary or wait in incidents until the body is cancelled or interrupted. The rejected alternative blocked the change that enables business keys on a family while an open body pins an unkeyed version.
4. **Entry failures use the exit REST and script entries use** (§6): the catch-all boundary at once, otherwise the default retries and then an incident on the `multiInstanceStart` job. The rejected alternative opened the incident at once without retries, as an ordinary call's failed start does.

## Implementation record (2026-11-13)

The call adapter is implemented on the TASK-05A foundation and follows the four
decisions above. The status stays **In progress**: the Worker-enabled browser
acceptance scenarios, several planned test cases, the scale run, and the prepared guide
sections and examples listed under [Still open](#still-open) are not complete.

### What changed

| Area | Change |
| --- | --- |
| Release boundary | `AutomaticMultiInstanceRules.InstalledAdapterKinds` now holds `callActivity`. A call body publishes and runs only on a Development host with the switch open; every other host refuses it at publication, at entry, in each item's `callStart` and `callResume`, and in the body's dispatch and completion jobs (a core change that covers service and script bodies too), including a body another host published. The job checks run once the job is known to own live work, so stale work closes quietly on any host. |
| Authoring rules | `CallActivityRules.InputSourceDeclarations` adds `mi.item` (the collection's element type, nullable only for json), `mi.index`, and `mi.total` (numbers) to a call body's input sources, spelled exactly; any other `mi.*` name, an output source, and an ordinary call keep the reserved-prefix rule. `FindTargetProblems` uses the same sources, so an optional json `mi.item` needs a child input that accepts null. A mapping from the body's own aggregate variable is refused, because staging leaves it out, and the editor's source field never offers it. The editor mirrors both (`callInputSourceDeclarations`, `CALL_ITEM_SOURCES`) inside the save-validator markers, with identical messages. |
| Schema | `CK_workflow_jobs_multi_instance_start_shape`, added `NOT VALID` by `20261113145806_AddMultiInstanceStartJobShape` and validated outside a transaction by `20261113145843_ValidateMultiInstanceStartJobShape`. No other schema change. |
| Entry | Routing queues a `multiInstanceStart` job before it reads any of the body's shared rows (the token waits `multiInstanceStart`; the entry counts once against the automatic-activation guard). `WorkflowEngineService.AutomaticMultiInstanceCalls.cs` processes it: the target family's lock, then the parent's runtime locks, one depth check, one published-default resolution and `FindCallTargetProblems` check, then `InitializeBodyAsync` with the pin or the target failure. An empty body completes with `[]` and no pin, even with no target. A nonempty body without a usable target, or past the depth limit, fails before any body row exists through the generalized `HandleAutomaticEntryFailureAsync`: the catch-all at once, otherwise retries and an incident whose type is the call code. A second token at the node waits as REST/script entries do. |
| Admission | The dispatch job admits through `AutomaticMultiInstanceCoordinator.DispatchCallsAsync`: ascending index within the window, the open-job limit, and the root's open-call limit. Each admitted item gets a reserved invocation with no inputs, its item and visit, and a `callStart` that names its body. A full root leaves items pending and parks the dispatch with the core's backoff (`callCapacity`), and so does a root with room for only part of the window: the dispatch admits what fits and keeps waiting, instead of completing and leaving the rest until one of the body's own items settles. Only a body with nothing in flight at the longest delay opens `call_capacity_exceeded`. |
| `callStart` | Two transactions. The parent-only stage captures shared inputs and output fences on the item (`CaptureItemContextAsync`), the `sys.*`, `config.*`, and `setting.*` values the local-output defaults and validations reference (read as the fixed `callActivity` actor, so `sys.user` is `callActivity` and no job or claim values exist), and the mapped inputs on the invocation, and commits; a failed mapping (`call_input_invalid`) commits nothing. The start takes the family lock first, fences again, validates the inputs against the pinned start, and starts the pinned version through the exact-definition start. Start and business-key failures take the catch-all or wait in a `callStart` incident that blocks the item; a retry reuses the saved stage. |
| `callResume` | Saves the mapped candidate (`MapAutomaticCallItem`: locals or readWrite shared aliases outside the body's collection, aggregate, and error targets; a missing optional output writes nothing; an explicit null must fit), commits, then publishes it, settles the invocation, and acknowledges the message in one transaction. A late result after settlement writes nothing. A shared output conflict opens `output_version_conflict` on the `callResume`; its retry publishes the saved candidate without starting the child again. An invalid result (`call_output_invalid`) takes the catch-all or waits in a `callResume` incident. A result too large for the item or the body is refused. A failure is found in one transaction and its incident opens in another, which checks the item's fence again, so an item a sibling's failure cancelled in between gets no incident. |
| Failures | A child that faults or is cancelled on its own takes the catch-all (partial result, then the boundary) or stops the body and faults the parent (`FailCallItemAsync`). `CaptureItemFailureAsync` settles the failed item's own invocation first, so abandoning the body's other calls sends no cancel message to a finished child. |
| Reads | Progress counts an item as blocked while the child-owned `callResume` delivering its result has an open incident. The item list and detail find a call item's job and open incident through its invocation (`callStart` or the child-owned `callResume`) and add `callInvocationId` and `childInstanceId`. The calls list adds `pinnedChildDefinitionId` and `pinnedChildDefinitionVersion` to an item's call, set before its child exists, and body progress adds `pinnedChildDefinitionVersion`; instance detail shows both. The per-token execution-position lookup ignores item invocations, so a body's position shows its progress, never one child. `JobStillOwnsItsWorkAsync` keeps a queued item's `callStart` from being completed as stale. |
| Editor | Call bodies offer `mi.item`/`mi.index`/`mi.total` and shared aliases as input sources and item-local outputs or readWrite shared aliases as result targets, with call-body help (pin, staging timing, root-limit sizing). The three call findings TASK-05A left open are closed: **Remove these options** keeps a valid body; clearing **Multi-instance** on a call is refused while a mapping reads an item or shared alias or writes a shared alias; and a `multiInstance` that is not an object is never shown as enabled, draws no marker, offers no timer or conditional boundaries, and offers **Remove the invalid value**. |
| Documentation | [AGENTS.md](../../AGENTS.md), [runtime reference](../../Flowbit/README.md), [deployment](../deployment.md#automatic-multi-instance-foundation), [database schema](../database-schema.md#automatic-multi-instance-foundation), [API reference](../api-guide.md#get-apiworkflowsauthoring-capabilities), [UI guide](../ui-guide.md), [TASK-05A](task-05a-multi-instance-core.md), the [parent](task-05-multi-instance-automatic-activities.md), and the [backlog](README.md). |

### Verification

- New PostgreSQL scenarios (`AutomaticMultiInstanceCallRuntimeTests`, 15) publish through
  an API host with the switch open and run every job through the registered processor,
  with real child workflows: a parallel body whose children finish in reverse order
  (ordered aggregate, one child per item, pin then release, no local becomes an instance
  variable, children started as `callActivity`); a sequential body that starts its second
  child only after the first result commits and keeps its pin after the family's default
  changes, while its pinned version cannot be unpublished or deleted; an empty body with
  no target (`[]`, no pin, no invocation); a nonempty body with no target (catch-all at
  once with `call_target_unavailable`, or a retry, with no body or invocation); a child
  fault (catch-all with a partial result and the child's fault code on the failed
  invocation, or a faulted parent); the root limit holding an item pending and admitting
  it when a child finishes; a full root with nothing in flight opening
  `call_capacity_exceeded` only at lease generation 5, then admitting after retry; parent
  cancellation settling every invocation and cancelling both children by message; a host
  outside Development refusing a published body's `callStart`, which waits in an incident
  that blocks its item; publication refusing a body outside Development and refusing a
  misspelled or mistyped item source; shared inputs captured per item (the next item reads
  the newer value) with a shared output conflict retried as publication only; an invalid
  local result waiting in a `callResume` incident that blocks admission, with no child
  started again; an interrupting timer cancelling waiting children and publishing no
  aggregate; the depth limit failing a nested body at entry; and an input rejected at
  staging (`call_input_invalid`), taken by the catch-all with no child, or waiting in a
  `callStart` incident with no stage, no inputs, and a queued invocation.
- Parity and unit tests: `EditorAutomaticMultiInstanceCallTests` (12 cases: six item-source
  rules on both sides, the accepted example, an ordinary call, item-source typing, an
  optional json item, the disable review, and a falsy import);
  `AutomaticMultiInstanceMigrationTests` adds the shape check's `NOT VALID` add, its
  validation and rerun, refusal of an activity-queue entry job, and rollback over a
  populated ordinary call. Tests that pinned call bodies as refused now pin a closed host
  refusing them (`AutomaticMultiInstanceScriptAdapterTests`,
  `AutomaticMultiInstanceScriptRuntimeTests`, `AutomaticMultiInstanceValuesTests`); the
  coordinator fakes record the failed item's invocation settlement; and
  `LifecycleEventCaptureTests` gives its `multiInstanceStart` row an instance and token,
  which the new check requires.
- Focused regression before the new tests were added
  (`FullyQualifiedName~AutomaticMultiInstance|CallActivity|Editor|Migration|LifecycleEventCapture|SharedVariable|VersionCompat`):
  1,478 of 1,479 passed; the failure was the lifecycle test's job row, fixed as above.
  The new and changed tests then passed together (65 of 65).
- Real browser (built-in Chromium pane): the editor folder served at
  `http://localhost:15801/flowbit-editor.html` by a local Node static server, remounted in
  hosted mode with the Development capability open (set up from script; the clicks,
  typing, and keys were real). On a parallel call body: the call and multi-instance
  markers drew on the node; **+ Add mapping** under Inputs focused the source field, whose
  suggestions were the workflow's variables, the two shared aliases, and `mi.item
  [json · item]`, `mi.index`, and `mi.total`; typing `mi.index` and Tab adopted `number`,
  and the workflow exported without errors; the result target suggested only
  `itemApproved` (item-local) and `lastApproved` (shared readWrite); after deleting the
  result mapping, clearing **Multi-instance** was refused with the alert naming `input
  mi.item, input mi.index`, and Ctrl+Z restored the deleted mapping; with an imported
  `roles` value, saving named it and **Remove these options** cleared it while keeping
  the body; an imported `multiInstance: false` showed **Multi-instance** unchecked, both
  warnings, no boundary section and no marker, and **Remove the invalid value** removed
  it. A sequential body drew its sequential marker. The inspector had no horizontal
  overflow at 1024×768, 1440×900, and 390×844 (where the call-body Inputs help was opened
  and read), and the console had no messages.
- Full solution: **5,075 passed, 0 failed**, in 13 minutes 51 seconds. Command:
  `dotnet test Flowbit/Flowbit.slnx -m:1`. The staging-rejection scenario was added after
  that run; the call runtime class then passed alone (15 of 15). The Chromium smoke and
  Worker acceptance suites were not run. Relative links and anchors in the ten changed
  documentation files resolve (1,771 checked), and `git diff --check` is clean.
- Review follow-up (2026-11-14). A review of the adapter against this plan found four
  defects, now fixed: a partial admission at the root limit completed its dispatch; an
  item incident could open after a sibling's failure had cancelled the item; an item's
  `callResume` did not check the release switch; and the editor treated any truthy
  `multiInstance` on a call as a body when offering boundaries and drawing the marker.
  Seven scenarios were added to `AutomaticMultiInstanceCallRuntimeTests` (now 22, all
  passing): a partial admission that keeps its dispatch waiting until another call under
  the root finishes; a sequential body that waits through a briefly full root without an
  incident; a closed host refusing a body at entry and an item's result, while
  acknowledging a stale result without an incident; a start failure after the stage is
  saved (a child business-key conflict), whose retry starts the child from the saved
  stage after the shared input changed; a failed entry that retries into a
  `call_target_unavailable` incident and initializes once the target is published again;
  a child cancelled on its own, caught or faulting the parent; and a second token waiting
  for the node's open body. `EditorAutomaticMultiInstanceCallTests` now has 15 cases
  (three imported values that are not objects, and the boundary section on a body). No
  test reproduces the item-incident race, which needs an interleaving the harness cannot
  force. Real browser: the editor folder served at `http://localhost:15801/flowbit-editor.html`
  and mounted in hosted mode from script; with real clicks, a call imported with
  `multiInstance: true` drew no marker and its inspector had no **Boundary events**
  section, while a valid body drew its marker, showed the section, and added a timer
  boundary from **+ Add timer boundary event**. No overflow at 1024×768, 1440×900, and
  390×844; the console had no messages. Full solution after these fixes: **5,086 passed,
  0 failed**, in 14 minutes 39 seconds (`dotnet test Flowbit/Flowbit.slnx -m:1`). The
  Chromium smoke and Worker acceptance suites were not run.
- Second review follow-up (2026-11-14). Five more defects, now fixed: a closed host ran a
  body's dispatch and completion jobs; the item API found no job or incident for a call
  item and named neither its call nor its child; call staging gave local-output defaults
  and validations no `sys.*`, `config.*`, or `setting.*` values, so `${sys.instanceId}`
  became `""`; publication accepted a mapping from the body's aggregate, which staging
  always leaves out; and no read showed the pinned version before an item's child
  existed. `AutomaticMultiInstanceCallRuntimeTests` now has 24 scenarios, all passing:
  a closed host retrying a body's dispatch and completion until an open host runs them,
  and a local default reading `sys.instanceId`, `sys.nodeId`, and `sys.user`
  (`callActivity`); the business-key and invalid-result scenarios now also check the
  item API's job, incident, call, and child for a `callStart` and a child-owned
  `callResume` incident, the calls list's pinned version before the child exists, and
  the body's pinned version. `EditorAutomaticMultiInstanceCallTests` adds the aggregate
  source to its server/editor cases (16). `AutomaticMultiInstanceProcessorTests` ran
  control jobs on the closed default test host and now uses one with the switch open.
  The automatic multi-instance and call suites passed 746 of 746, and the full solution
  **5,089 of 5,089**, in 15 minutes 2 seconds (`dotnet test Flowbit/Flowbit.slnx -m:1`).
  The Chromium smoke and Worker acceptance suites were not run.
- Real browser for those fixes (built-in Chromium pane): published API, UI, and Worker
  in Development with the switch open, a disposable PostgreSQL container, the UI at
  `http://127.0.0.1:15452`, and a parent whose two items map the same business key into
  a keyed child, published over HTTP. On instance detail, the blocked item's call read
  **Version 1, pinned by the multi-instance body** and the body row **Calls version 1 for
  every item**; **Show items** listed item 0's call, a **Child instance #2** link (which
  opened the child), and item 1's call with **Incident #1**. After **Take action** on the
  child's task, **View item 0** showed the committed `origin` as `1/2/callActivity`. In the
  hosted editor the source suggestions omitted `orderResults`; typing it and **Save new
  version** showed the aggregate-target error, and Ctrl+Z returned the editor to
  **Saved**. No page overflow at 1024×768, 1440×900, and 390×844; the console had only
  Blazor connection messages.

### Still open

- Worker-enabled browser acceptance scenarios for call bodies in
  `Flowbit/tests/Flowbit.BrowserAcceptanceTests/CallActivityAcceptanceTests.cs`, and the
  manual browser steps 2–8 of §9 against published API, UI, and Worker hosts, with
  screenshots of the queued, running, blocked, completed, and cancelled screens. The
  browser check above covered the editor only.
- Planned tests not written yet: non-interrupting timer and conditional boundaries on a
  call body, and a condition interrupting a queued entry; duplicate `callStart` and
  `callResume` leases and simultaneous last results, including the race the item-incident
  fence closes; a child's internal incident holding
  its slot; a missing or archived shared binding at staging; a pinned version that a newer keyed version makes
  unstartable (decision 3); an output too large for the item or the body result; a body
  whose children's own calls are refused while it holds the root (decision 2); a shared
  typed collection; version-switch and reactivation blockers with an open body; and
  retention while a child has succeeded and its parent still waits.
- A scale run with production log levels.
- Outside this adapter, for TASK-05B and TASK-05C: a REST or script item job checks the
  release switch before its fence, so on a closed host a stale item job ends in an
  incident instead of closing; and no rule refuses a service template or a script that
  reads the body's own aggregate, which items never receive.
- The prepared call sections of the behavior guides (node reference, BPMN support,
  developer guide), the AI authoring guide's call rules, and the paired examples, merged
  in the parent's integration change (§10). Until then the definitions live in the tests,
  and the node reference still states the released contract: `multiInstance` is invalid
  on a call.
