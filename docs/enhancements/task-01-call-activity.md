# TASK-01: Call activity (reusable subprocess)

[Enhancement backlog](README.md) · [Documentation home](../index.md)

| Field | Value |
| --- | --- |
| Status | Done |
| Category | New task type |
| Effort | L |
| Priority | Conditional: schedule when a concrete reuse case justifies its lifecycle complexity |
| Depends on | None |
| Related | [TASK-05](task-05-multi-instance-automatic-activities.md), [EVENT-05](event-05-error-code-boundaries.md), [EVENT-07](event-07-compensation.md) |

## Summary

A `callActivity` node starts a child instance of another workflow family and waits until it finishes. The first release selects the family's published default version and saves the resolved version for that invocation. Typed input mappings copy parent values into the child's start variables, and typed output mappings copy the child's results back to the parent when the child completes. Cancelling a parent settles its calls at once and stops its descendants through durable jobs. Latest-published and exact-version bindings are later work.

This page is the plan this feature was built from, kept for its decisions. See the [node reference](../node-reference.md#callactivity), the [developer guide](../developer-guide.md#call-another-workflow), the [API reference](../api-guide.md#get-apiinstancesidcalls), and [deployment](../deployment.md#call-activity-rollout) for current behavior, and [implementation evidence](#implementation-evidence) for what was and was not verified.

## Why it adds value

- Shared fragments, such as a manager approval chain, a KYC check, or a payment step, are modeled once and reused instead of copied into every workflow.
- Diagrams stay smaller, and each fragment is versioned and published on its own.
- It provides explicit lifecycle ownership for reusable workflows instead of asking integrations to coordinate independent instances themselves.

## First release acceptance criteria

The plan expands the original criteria to cover durable execution, authorization, races, and recovery. A Worker is required even when both workflow definitions otherwise contain only synchronous nodes.

- [x] A `callActivity` names a workflow key. The Worker resolves its published default and default plain start event once, and atomically saves the resolved definition, start event, and single child instance for that invocation.
- [x] Typed input mappings snapshot parent values at call entry. Child start validation/defaults remain authoritative. Output mappings snapshot child results at terminal commit and update the parent all-or-nothing on successful delivery.
- [x] Each visit has a distinct durable invocation. Retries, duplicate leases, immediate child completion, restarts, and repeated visits cannot create duplicate children or deliver the same result twice.
- [x] The parent token rests at the call until a durable result is delivered. Its successful continuation and output writes commit once; other parent branches may continue while it waits.
- [x] Parent cancellation, termination, fault, and cancellation of a hosting gateway branch settle that branch's invocations in the same transaction, so no later result can change the parent and no descendant can begin a new transition. The fence checks complete persisted ancestry even after `MaxDepth` is lowered. Durable jobs then cancel the running descendants. Completed children retain their terminal state.
- [x] Child faults and independent child cancellation take the call's single catch-all error boundary, or durably fault the parent when no boundary exists. Failures before the child exists and invalid outputs take the boundary when there is one; otherwise they open a retryable incident and the parent keeps waiting. A child's `terminateEndEvent` is successful completion of that child only.
- [x] The child and the parent's continuation run as the fixed system actor `callActivity`, without inherited roles, claims, or delegation. First-release targets have an unrestricted plain start and no header-based start idempotency; normal role and business-key checks are not bypassed. Only the default plain start may be marked `callOnly`, and it cannot be started through public routes. Both `callActivity` and `callOnly` are protected by the publication gate and rollout restrictions.
- [x] Depth is limited exactly at admission, and admitted open invocations use the documented soft threshold and concurrency bound. Calls blocked before admission consume no reservation; retries reuse an existing reservation or acquire one after capacity becomes available. Every call entry counts against the automatic-activation guard. In-place version changes and reactivation cannot alter an unfinished call or replay a settled child.
- [x] Instance detail shows a parent link, root link, and paged call history with invocation state and pinned child version. Relations grant no task-action authority.
- [x] Administrators can see which published workflows call a family, and whether they remain compatible, before changing that family's default. Deletion preview evaluates the actual eligible successor or explicitly reports that no published default would remain.
- [x] Shared editor authoring, import/export, undo/redo, read-only diagrams, API/OpenAPI contracts, migration/retention behavior, automated tests, real-browser checks, and owning documentation are complete before marking this item Done.

## Later releases

- Latest-published and exact version bindings.
- Selecting a non-default, message, or timer start; invoking starts with role restrictions or header-idempotency contracts.
- Caller-identity propagation and a separately designed authorization contract for protected callable workflows.
- Shared-variable aliases in call mappings, nested JSON extraction, expressions, and mapping-specific defaults/validation.
- Timer and conditional boundaries on a call activity.
- Multi-instance call activities ([TASK-05](task-05-multi-instance-automatic-activities.md)).
- Named error-code matching for child faults. [EVENT-05](event-05-error-code-boundaries.md) shipped HTTP status matching, not child/business-error matching.
- Direct self-calls, which this release rejects.
- Public call retry/detach/reparent operations, direct reactivation of linked children, recursive tree APIs, and parent/root filters in general instance search.
- Compensation of external side effects.

## Current state

What follows is the state of the repository when this plan was written, on 2026-11-01, kept because the design decisions rest on it.

- [Unsupported capabilities](../bpmn-support.md#unsupported-capabilities) listed call activities and reusable subprocess invocation.
- The [database schema](../database-schema.md) had no parent/child relationship between workflow instances.
- [WorkflowModel.cs](../../Flowbit/src/Flowbit.Shared/Models/WorkflowModel.cs) had no call node or invocation configuration. The editor and server maintain explicit node-type and boundary-host rules.
- [WorkflowEngineService.cs](../../Flowbit/src/Flowbit.Service/Services/WorkflowEngineService.cs) resolves published defaults in `StartInstanceCoreAsync`, checks start roles, collects typed inputs, and enforces separate transport-idempotency and business-key contracts. It also routes the new instance, so it is not a safe recursive child-start API inside an arbitrary parent transition. `ProcessTimerStartJobAsync` already calls it from a job with `joinAmbientTransaction` and `forceDurableActivities`, which is the model for child creation.
- [WorkflowDefinitionRepository.cs](../../Flowbit/src/Flowbit.Infrastructure/Repositories/WorkflowDefinitionRepository.cs) acquires workflow-family locks before starts. Runtime mutations use instance, gateway, token, multi-instance, task, then shared-variable locks, almost all through `GetInstanceForUpdateAsync`. Those locks are `FOR UPDATE`, so inserting a row with a foreign key to a locked instance or token waits for the lock holder.
- [WorkflowEngineService.Jobs.cs](../../Flowbit/src/Flowbit.Service/Services/WorkflowEngineService.Jobs.cs) already supplies durable waits, leases, actor snapshots, and execution fences. `CK_execution_tokens_wait_shape` requires a token's wait state to own a job or timer subscription. `JobStillOwnsItsWorkAsync` completes a conflicting job as stale unless the token is waiting on that job. [WorkflowDefinitionReadinessChecker.cs](../../Flowbit/src/Flowbit.Service/Services/WorkflowDefinitionReadinessChecker.cs) gates durable publication, but does not recognize call activities.
- `Workflow.Async.MaxConsecutiveAutomaticActivations` bounds loops of automatic, service, and script activations. `WorkflowJobCapacity` caps each instance's open jobs, and `EnqueueInstanceJobAsync` throws at that cap.
- Error boundaries currently support service/script hosts. Their `errorVariable` is a scalar string; HTTP matchers apply only to REST hosts. `errorEndEvent` faults its own instance, while an exhausted durable job may remain an operational incident rather than a terminal instance fault. `FaultInstanceAsync` and token fault metadata assume an `errorEndEvent`, and faulted instances cannot be reactivated.
- `ValidateEntryProcessVariableCollisions` forbids a start variable from also being an instance process variable. Declared process variables are persisted at start, so a nullable one that was never set is a present null rather than missing.
- `set-default` can select any published version, and publish and set-default return `204` with no body. Nothing relates a definition to the workflows that would call it.
- Instance detail currently requires an authenticated actor but does not apply personal-task visibility filtering; see [authentication boundaries](../developer-guide.md#authenticate-the-actual-actor). Adding relationships must not imply a new ownership or tenant boundary.

## Builds on

- Typed start-variable contracts and the validation/batch-write rules used by REST and message output mappings; call mappings have their own whole-variable source shape.
- Published-default version resolution, already used by message starts and timer starts, and the job-owned start in `ProcessTimerStartJobAsync`.
- Error boundaries, instance cancellation, and `terminateEndEvent` cancellation for child failure and parent-to-child cascades.
- Durable job fencing and unique persisted activation ownership for retry safety. Public start idempotency and business keys remain separate contracts and cannot substitute for invocation identity.

## Open questions

The first-release decisions are resolved as follows; changing them during implementation requires updating this plan and its acceptance tests.

| Question | Decision |
| --- | --- |
| When is the child version selected? | When the `callStart` job creates the child, under the target family's lock. Save the immutable definition ID and selected start-event ID. A queued call has not pinned a version yet. |
| Can either instance change version during a call? | Block a parent with unsettled outgoing calls and a child with an unsettled incoming call, including queued calls and calls whose result awaits delivery. Completed history alone does not block later compatible switches. |
| Which identity starts the child? | `callActivity`, with empty roles/claims, no delegation, and no JWT audit claims. Require the selected start's roles to be empty and still execute the normal role check. Record parent causation separately. |
| Which identity continues the parent? | `callActivity`, as a timer catch continues as `timer`. `callResume` and `callCancel` ignore job payload actors, so a child's approver or message client never reaches the parent's `sys.*` context or flow evidence. |
| Can users start a callable workflow directly? | Yes by default, because an unrestricted start is open to every authenticated user. The default plain start may set `callOnly: true`; public start routes and the start page then refuse it. A non-default `callOnly` start is invalid in this release, because calls cannot select it. Use the flag for fragments such as payment or KYC that rely on checks in the parent. |
| How does cancellation reach descendants? | Through durable `callCancel` jobs. The parent's transaction settles its invocations immediately, a fence stops new descendant transitions, and each job cancels one child and queues the next level. No transaction locks two existing instances. |
| What reaches an error boundary? | A bounded scalar-string diagnostic containing the stable call failure code and safe description. Store structured child IDs/outcome/error-code metadata in invocation audit; do not introduce structured boundary variables or HTTP matching. |
| What happens to a failure with no boundary attached? | A child fault or independently cancelled child faults the parent. A failure before the child exists, or an invalid output, opens a retryable incident and the parent keeps waiting. Faulted instances cannot be reactivated, so faults are reserved for child outcomes. |
| What bounds recursion and fan-out? | Deployment options `WorkflowCallActivities:MaxDepth` (default 8, range 1–32) and `MaxOpenInvocationsPerRoot` (default 256, range 1–4096). Root depth is 0; the maximum is inclusive for children at admission. The open-call setting is a soft admission threshold for reserved unsettled calls, with concurrency behavior specified in section 8; blocked records consume no slot. A direct self-call is rejected, and each call entry counts against `Workflow.Async.MaxConsecutiveAutomaticActivations`. |
| How do callers learn that a child change breaks them? | A new route lists published callers and whether each passes the contract check. Set-default preview checks the candidate; deletion preview uses the command's actual successor-selection rules and reports no remaining default explicitly. The Workflows page shows incompatible callers in the confirmation. Compatibility warnings do not add command blockers; existing deletion rules still apply. |
| How are relationships searched? | Add detail links and a paged calls endpoint. General instance list/search continues to return independent instances; new relation filters are deferred. |
| Does the first release need a Worker? | Yes. Child creation, result delivery, and descendant cancellation cross durable boundaries; no inline or Worker-free call mode. |
| What happens to a child incident? | The parent continues waiting. A retryable/operational incident is not a terminal child fault. Existing incident recovery remains available and cannot create a replacement child. |
| Can a child be reactivated? | Direct reactivation of any linked child is rejected in this release. Root reactivation follows existing rules only after all its calls settle and every descendant has stopped, with fresh visits for future calls. |

## Plan

### 1. Scope and execution invariants

Implement reusable workflow invocation, not embedded subprocesses. A child remains a normal instance with its own variables, history, tokens, tasks, definition version, business key, and operations. One invocation owns at most one child, and one child belongs to exactly one invocation. Root instances have no incoming invocation. Calls may appear in loops and parallel branches and may nest. A direct self-call is rejected; depth limiting bounds indirect recursion rather than attempting to prove a global acyclic workflow-family graph.

Preserve these invariants:

1. A call is a resting node, not a user task, inbox item, or synchronous pass-through task. It has at least one incoming flow and exactly one unconditional normal outgoing flow without submitted variables, action roles, or gateway flags.
2. At most one attached interrupting catch-all `errorBoundaryEvent` is supported. Timer/conditional boundaries, `errorMatch`, `multiInstance`, authored `asyncBefore`/`asyncAfter`, and node job policies are invalid on this release's call contract. Durability is intrinsic.
3. Inputs are copies taken at parent entry; outputs are copies taken when the child becomes terminal. There is no shared local-variable scope, live binding, or implicit transfer of all variables.
4. An invocation can start its child once and settle its parent once. A new visit to the same node gets a new activation and may start a new child.
5. Parent success requires successful child completion and valid output delivery. Normal child end and child terminate end qualify; child fault or independent cancellation do not.
6. Abandoning a call is atomic on the parent: its invocation settles in the same transaction that cancels the token, and no later result can change the parent. Durable jobs then cancel the descendants. External side effects already performed by child service tasks are not undone, and an in-flight HTTP request cannot be physically recalled; stale finalization is fenced out.
7. A transaction locks the runtime rows of at most one existing instance. The only other instance it may touch is a child it is creating, which no other transaction can see yet.
8. No public HTTP request recursively calls the start endpoint, no in-memory subscription owns completion, and no status-polling loop is required for correctness.

### 2. Workflow JSON and mapping contracts

Add `BpmnFlowNodeTypes.CallActivity`, `IsCallActivity`, and nullable `FlowNodeModel.CallActivity`. Proposed shared types are `CallActivityModel` and `CallVariableMappingModel`; keep them separate from REST/message mappings because `sourceVariable` names one complete variable, not a dotted response path.

| Property | First-release contract |
| --- | --- |
| `callActivity.workflowKey` | Required stable workflow-family key, with the same length/casing/normalization rules as existing workflow-key selection. No interpolation. Cannot equal the definition's own `id`. |
| `callActivity.versionBinding` | Omitted means `publishedDefault`; this is the only accepted value. The editor omits the default on export. Unknown values fail validation. |
| `callActivity.inputMappings` | Optional array, at most 100 entries. Copy parent variables into declared inputs of the target's default plain start. |
| `callActivity.outputMappings` | Optional array, at most 100 entries. Copy child variables into declared parent process variables after successful completion. |
| Mapping `sourceVariable` | Required whole variable name stored on the source instance. Resolve using existing variable-name rules and persist canonical declared spelling. |
| Mapping `variable` | Required destination name. Duplicate destinations within the same direction are rejected using existing case-insensitive variable-name uniqueness rules. |
| Mapping `dataType`, `isArray` | Required supported scalar type and optional Boolean array flag (omitted means false). Must agree with the destination's declaration and with a declaration of the source. No implicit string/number/array coercion. |
| Mapping `required` | Optional Boolean, default false. When true, a missing source or JSON null fails the call, matching REST and message mappings. When false, a missing source omits that input or leaves that parent output unchanged. It never weakens a child's required input contract. |
| Start event `callOnly` | Optional Boolean, default false, omitted on export when false. When true, valid only on the `startEvent` selected by `initialEventId`, with empty `roles` and no `idempotency`; only a call activity can start from it. Reject a non-default call-only start rather than saving an event with no supported entry route. |

Proposed node fragment (not a complete runnable definition):

```json
{
  "id": 12,
  "type": "callActivity",
  "name": "Manager approval",
  "laneId": 1,
  "x": 460,
  "y": 180,
  "callActivity": {
    "workflowKey": "manager-approval",
    "inputMappings": [
      {
        "sourceVariable": "requestedAmount",
        "variable": "amount",
        "dataType": "number",
        "required": true
      }
    ],
    "outputMappings": [
      {
        "sourceVariable": "approved",
        "variable": "managerApproved",
        "dataType": "boolean",
        "required": true
      }
    ]
  }
}
```

The parent collects `requestedAmount: number` at its start event and declares a nullable process variable `managerApproved: boolean`. The child declares a plain-start input `amount: number` and a nullable process variable `approved: boolean`. If the child finishes with `approved` still null, the required output mapping fails the call.

Mapping rules:

- A source may be any variable the source workflow stores on its instance: a process variable, an entry variable, a user-task flow variable, or a REST or message output target. Restricting sources to process variables would force a copying script for every start value, because `ValidateEntryProcessVariableCollisions` forbids a start variable from also being an instance process variable. Validation rejects a name the workflow never declares or produces, and a mapping whose `dataType`/`isArray` disagrees with every declaration of that name. A name may be declared more than once, so the actual value is always checked at runtime. Reject shared aliases and reserved context names as sources. Child workflows may still use their own shared bindings internally.
- Parent output targets must be declared instance-scoped process variables, so each has one type, nullability, and validation contract. Child input destinations must be declared on the selected start and cannot be its transport-idempotency variable. Reject undeclared destinations, shared aliases, and reserved context names.
- Child required inputs need mappings with `required: true`. Unmapped optional inputs use the child's existing default handling. Resolve those defaults and validations once during child creation, with child context, and save the resulting inputs; retries do not reevaluate a committed start.
- For an optional mapping, missing and JSON null are distinct. A present null is passed on, never omitted or replaced by a default. Preserve existing type/nullability rules: plain-start inputs cannot declare `nullable`, but an optional scalar `json` input accepts JSON null under existing typing; required inputs still reject null. A whole output null is valid only for a nullable parent process target. Static nullable-source checks must account for that optional-JSON exception; check actual values again at runtime, including existing JSON-array element rules.
- Call mappings do not add expressions, paths, `defaultValue`, or mapping `validation`. Run the child start's existing input validation over the complete input overlay; run the parent's process-variable validations over the complete output overlay. Validate all outputs before writing any.
- Input and output snapshots are each limited to 1 MiB, the value of `DefaultMaxSnapshotBytes`. An oversized input is `call_input_invalid` and an oversized output is `call_output_invalid`. Internal jobs do not bypass the limit.
- Reject overlap between the call's output targets and its attached boundary's `errorVariable`. Existing boundary error-target rules still apply; this restriction does not ban shared variables elsewhere in either workflow.
- Once captured, source values do not change with subsequent administrative updates or sibling activity. At delivery, valid outputs overwrite the latest parent values under its lock, consistent with existing output producers. Do not silently merge JSON or add a new optimistic value-conflict policy.
- Route output writes through the existing variable mutation tracker, so dependent conditional subscriptions observe one complete batch. If that batch interrupts the call's enclosing scope, cancellation fences win and the normal continuation is suppressed.

### 3. Definition validation, compatibility, and publication

Update `WorkflowDefinitionValidator`, `WorkflowModelMigrator`, the editor loader/exporter/save validator, and `WorkflowAuthoringJson` together.

**Local checks** validate configuration shape, bounds, node topology, parent declarations, unsupported properties, error-boundary hosts, self-calls, and `callOnly` placement, including its default-start requirement. Changing `initialEventId` while leaving `callOnly: true` on the previous start is invalid; require an explicit flag edit rather than silently clearing or moving it. Reject a missing/null call config, malformed mappings, unknown binding values, or a config on the wrong node type. Validate incoming JSON before a normalizer can drop invalid fields. Add strict parsing for the new subtree where necessary, as `ErrorBoundaryMatchModel` already does: API and Flowbit.Ui use Web JSON defaults that otherwise accept numeric strings and ignore unknown properties. Test raw HTTP JSON as well as typed objects. Preserve malformed editor imports for actionable validation or reject the import without replacing the current document; never turn invalid settings into defaults silently.

Old definitions have no call property and retain their serialized behavior. The new properties are omitted when absent; no synthetic call config is added during legacy migration. Explicit type changes away from a call clear its config and unsupported boundaries through the existing confirmation/history path; simply selecting a node must not erase invalid imported configuration. Preserve the save validator's established phase order and existing characterization arrays; add call checks in a documented stable position.

**Cross-definition checks** belong in a focused call-contract checker used by `WorkflowDefinitionReadinessChecker` and runtime creation:

1. Draft save validates local structure and parent contracts without requiring the child to be published, allowing child-first or parent-first drafting. Readiness reports unresolved target requirements explicitly.
2. Publish and set-default validate the currently published child default, its default plain start, empty start roles, absence of start header idempotency, mapping declarations, required inputs, type/nullability compatibility, and reusable shared/durable readiness rules.
3. Actual child creation repeats these checks under the target family lock. A changed default may make a previously published caller incompatible; fail that invocation through section 7 instead of selecting a different version or dropping mappings. Changing a child default does not automatically republish or globally freeze all callers.
4. Include both `callActivity` and `callOnly: true` in `EnsureDurablePublicationAllowed`, even when authored async flags are absent and a call-only target otherwise contains only synchronous nodes. When durable publication is disabled, either definition may remain a draft but cannot become published/default. Apply this gate to create-with-publish, update-with-publish, publish, set-default, and deletion-driven default promotion; the last must reject a gated successor rather than silently choosing a different version. This also protects the new start restriction during rollout; legacy APIs cannot enforce the gate for an unknown flag, so section 13 additionally freezes authoring of the new configuration until the fleet is upgraded.
5. Do not recursively expand the entire referenced family graph at publication. Validate each immediate target; bound actual nesting at runtime. Because publication needs a published target, mutually recursive families can only be published in stages: publish one without the call, publish the other, then publish a new version with the call. The depth limit then stops them at runtime.

**Caller visibility.** Write `workflow_definition_call_targets` rows when a definition version is saved, following the business-calendar references. `GET /api/workflows/{workflowKey}/callers` lists published caller versions and whether each passes the contract check against the current default, an optional `candidateId`, or the result of deleting the current default with `deletingId`. The last two selectors are mutually exclusive. Extract a shared read-only deletion assessment/successor resolver used by both this preview and `WorkflowDefinitionRepository.DeleteAsync`: preserve existing reference blockers, business-key and required-distributor constraints, and selection of the highest-version eligible remaining published definition. Include the publication gate from this section; do not approximate successor selection in the UI.

For an allowed deletion, return the resolved successor ID/version, or an explicit `noDefault` result when none would remain; every caller is incompatible with `call_target_unavailable` in the latter case. For a blocked deletion, return the existing blockers and identify the current default as unchanged, without suggesting that a successor will be applied. The Workflows page uses the appropriate preview before set-default or deletion and names incompatible callers in the existing confirmation. Previews are advisory snapshots, take no persistent reservation, and may become stale; the command rechecks its existing rules and successor under the family lock. Compatibility warnings do not add blockers, and successful commands keep their `204` responses.

Update `SharedVariableAccessPlanner` and `SharedVariableTransactionLockOrderValidator`: call entry is a durable resting boundary, and output delivery starts a new transaction segment before the parent's normal or error continuation. Include boundary error outputs in producer/lock analysis. Keep cached definitions immutable.

Reuse this readiness in AI validation, reporting target problems as publication blockers, without changing agent instructions, tool names, tool descriptions, or adding tools. Add an optional `CallTargetKeys` selection beside `SharedVariableKeys`, so `WorkflowAiAuthoringService` can give the model the value-free start inputs and declared results of the selected families. Without it the agent can only guess mappings and read the resulting errors.

### 4. Persistence and migration

Add an additive migration, proposed as `AddCallActivityExecution`, under the fixed `flowbit` schema. Update entities, repository ports/implementations, `AppDbContext`, and its model snapshot together.

| Object | Planned fields and constraints |
| --- | --- |
| `workflow_instances` | Nullable `CallRootInstanceId` and non-null `CallDepth` default 0, constrained to 0–32 independently of the configured admission limit. Roots/legacy rows have null root and depth 0; children have the owning root ID and positive depth, enforced by a root/depth shape check. Root/depth are immutable after insertion. Index root plus status for running descendants. The root ID is a plain column rather than a foreign key, so creating a nested child never waits on the root's row lock. |
| `call_activity_executions` | Durable invocation ID; parent instance/token/node IDs; independent activation ID; optional node-execution ID for audit; root ID/depth; parent definition ID; target workflow key; nullable resolved child definition/start IDs and child instance ID; state; `CapacityReserved` Boolean default false; captured call contract and input values; bounded failure metadata; causal audit; creation/admission/start/settlement timestamps. Written only under the parent's instance lock. |
| `call_activity_results` | One row for a child that finished while its invocation was unsettled: child instance ID as key, invocation ID, call outcome, captured output values, bounded failure metadata, child fault code, capture time. Written once under the child's instance lock, then immutable. |
| Invocation uniqueness | Unique `(ParentInstanceId, ParentTokenId, ActivationId)` and unique non-null `ChildInstanceId`. Use restrictive instance/definition FKs and checks for legal states. A re-entered node always has a new activation. |
| Invocation indexes | Parent plus invocation ID for paged history; child lookup for the fence; parent token plus unsettled state for abandonment; root with a partial predicate for reserved unsettled calls for admission counting. Index selective open work rather than scanning all historical visits. |
| `workflow_definition_call_targets` | Definition ID, node ID, and target workflow key, with an index on the target key. Rows go with their definition. |
| Durable jobs | Add kinds `callStart`, `callResume`, and `callCancel`, and nullable `CallInvocationId` on `workflow_jobs` with a unique `(CallInvocationId, Kind)` index. Do not rely on JSON payload alone for uniqueness. |
| Token wait | Add wait state `callStart`, owned by the queued job. It equals the job's `Phase`, which the automatic-loop retry requires. While the child runs the token has no wait state or job, because `CK_execution_tokens_wait_shape` forbids a wait state without one. It rests as a user task does, and the invocation row records what it is waiting for. |

Stored invocation states are `queued` (no child yet), `running` (child created), and settled `completed`, `failed`, or `cancelled`. `failed` means the call's failure was delivered, whether caught by a boundary or used to fault the parent. `cancelled` means the owning parent activation abandoned it; it is not the same as independent child cancellation. APIs report `resultReady` for a `running` invocation that has a result row; it is a projection, because a child's transaction never writes the invocation. A result row records a call outcome of `completed`, `faulted`, or `cancelled`. Call outcome is distinct from child status: invalid captured outputs give a `faulted` outcome with `call_output_invalid` while the child remains completed.

Admission is separate from lifecycle state. A queued invocation blocked by input, depth, or root capacity checks has `CapacityReserved = false`, no child, and a `callStart` incident. A successful admission sets the flag and admission timestamp in the same transaction as its queued work. Once admitted, retries retain that one reservation until settlement; every settlement path clears it atomically. Add checks requiring a reservation for `running`, forbidding one on settled rows, and permitting an unreserved unsettled row only while queued with no child. Unreserved rows still participate in history, cancellation, version/reactivation blockers, and incident recovery; they are excluded only from admission counts. The ordinary job limit and automatic-activation guard still apply to their work.

Store the canonical call contract and input snapshot on entry, the exact child/start/input result when creation commits, and only mapped output values plus safe terminal metadata in the result row. Preserve missing-value markers separately from explicit null. Never store JWTs, provider keys, arbitrary exception dumps, or all child variables in these rows.

The identity and result must survive history retention. Do not use a restrictive FK to prunable `node_executions` as the sole ownership fence; use an independent activation ID, and allow optional audit references to become null when their rows are pruned. Retain a compact invocation/relationship tombstone for instance lifetime. Keep required snapshots until settlement, then make bulky captured values eligible under the existing `workflowHistory` retention policy, aged from the owning parent's terminal time; do not add an automatic early purge or a new retention category. Open call jobs, unsettled result rows, or referenced definitions must not be removed by job/history cleanup. Extend `RetentionRepository` and definition deletion checks, including queued calls that reference a parent definition before any child exists.

Existing instances receive only root/depth defaults; no fake call history is backfilled. A down migration must refuse while call instances, invocation rows, or call job kinds remain. Operational rollback after use is a forward-compatible repair or database restore, not dropping live relationship tables.

### 5. Transaction and lock protocol

The design adds no new lock and leaves the existing runtime lock order unchanged. It keeps cross-instance work out of any single transaction:

1. **One existing instance per transaction.** Every transaction locks the runtime rows of one existing instance, in the established instance, gateway, token, multi-instance, task, and shared-variable order. The only second instance it may touch is a child it is creating, which no other transaction can see yet.
2. **One writer side for each row.** Invocation rows are written only while holding the parent's instance lock, which already serializes those writes, so they need no row locks of their own. Result rows are written once while holding the child's instance lock and never change. Each side reads the other's row without locking it.
3. **Cross-instance jobs are messages.** `callCancel` goes from parent to child, and `callResume` goes from child to parent. The job row belongs to the sender's instance and token, because those are the rows its transaction already holds, and names the invocation in `CallInvocationId`. No row written by one side may hold a foreign key to the other side's instance or token: a foreign-key check takes `FOR KEY SHARE` on the referenced row, and `GetInstanceForUpdateAsync` holds instances and tokens `FOR UPDATE`, so the insert would wait for the other side's transaction. Settling an invocation changes only non-key columns, so it does not conflict with those checks on the invocation row. Processing a job locks only the receiver.
4. **Family lock first.** `callStart` takes the target family's shared start lock before the parent's runtime locks, the order version changes and timer starts already use. Never acquire a family lock later in a routing transaction.
5. **Shared rows stay last.** The per-definition, per-segment lock-order proof is unaffected. Because no transaction holds the rows of two running instances, shared rows cannot form a cycle through a call.

**The call fence.** After locking an instance with a non-null `CallRootInstanceId`, `GetInstanceForUpdateAsync` reads its complete chain of incoming invocations without locks, in one recursive query. Traverse according to persisted `CallDepth`, bounded by the fixed supported maximum of 32, never the current configured `MaxDepth`: lowering the admission limit must not shorten an existing descendant's cancellation fence. Require exactly the persisted number of links, decreasing depth by one at each link, ending at the recorded root with depth 0. Detect missing links, cycles, inconsistent root/depth, and truncation; fail closed as an invariant failure, with no transition, and preserve job work through an operational incident rather than marking it stale. A valid chain with any `cancelled` invocation instead throws a dedicated `WorkflowConflictException` subtype: API commands return `409`, and jobs complete as stale instead of opening a `job_conflict` incident. `callCancel` and instance cancellation bypass the fence, because they are what finishes the work. Roots skip the query, so instances outside a call tree pay nothing. The read takes no lock, so a transition already in progress may still commit just after the parent's cancel; its result is discarded at delivery. `RetryIncidentAsync` locks an instance directly rather than through that method, and a retried job meets the fence when it runs.

**What this trades away.** Descendants stop accepting new work when the parent's transaction commits, but they do not become terminal until their `callCancel` jobs run: normally moments later, longer with a backlog, and not at all while Workers are stopped. Until then their tasks stay visible in My work, actions on them return `409`, and `POST /cancel` has already returned. In exchange, a parent's cancel never waits for a child's transaction, including a synchronous REST call of up to `WorkflowServiceTasks:MaxTimeoutSeconds`; one transaction's size does not grow with the tree; independent children serialize against each other only on shared variables they both use; and instances outside a call tree see no new lock or query.

PostgreSQL race tests covering concurrent parent and child writers are a release gate. Do not add a cross-instance lock to tighten this behavior without revising the acceptance criteria.

### 6. Durable engine lifecycle

Keep HTTP orchestration out of the implementation. Introduce focused call coordinator/repository abstractions, with an engine partial such as `WorkflowEngineService.CallActivities.cs` for routing integration. Reuse `StartInstanceCoreAsync` through the job-owned path `ProcessTimerStartJobAsync` already uses, adding only what calls need: an input overlay, the fixed actor, the call link, and permission to use a `callOnly` start. Preserve existing public start behavior.

**A. Parent enters the call.** In `ResolvePassThroughAsync`, before the generic resting-node branch and idempotently per activation: allocate the ordinary node-execution visit and fresh invocation identity, validate/capture input sources, check depth and root admission capacity, reserve one slot, persist the queued invocation, set the token's `callStart` wait, and enqueue `callStart` in the same commit. Enqueue through `EnqueueInstanceJobAsync` with `countAutomaticActivation`, and add the call type to `IsGuardedAutomaticActivity`, so the existing loop guard opens its retryable incident. The normal per-instance job cap applies here, as it does to any durable activity. Return at a resting position. If input capture, depth, or root capacity fails before admission, follow section 7: record the failed invocation and route an attached boundary in this transaction, or persist it queued with `CapacityReserved = false` and the job with an open incident through `EnqueueIncidentAsync`. Preserve any successfully captured inputs. A rejected admission must not reserve capacity merely to record its failure. The parent's other active tokens remain independent.

**B. Worker creates and routes the child.** One transaction, following `ProcessTimerStartJobAsync`: read the target key from the invocation, take the target family's shared lock, take the parent's locks and its fenced token and job, and recheck that the invocation is still queued. Capture inputs if a retry after a failed entry still needs them. An unreserved retry repeats depth/capacity admission and sets `CapacityReserved` before child creation; an admitted retry reuses its existing slot without comparing its own reservation to the limit or acquiring another. Resolve the published default and its `initialEventId`, check target readiness and start contracts, and call the start path with `joinAmbientTransaction`, `forceDurableActivities`, and the fixed actor. It collects typed child inputs/defaults, reserves the child's own business key, and inserts the child with root/depth. Link the invocation to the child immediately after that insert and before routing, so a child that finishes in this pass already has its call link. Route the child, set the invocation `running`, clear the token's wait, complete the job, and commit together. No commit may contain an orphan child or an invocation pointing at an uncommitted child, and a retry finding the link never starts another child.

Forced durability means this pass runs no service or script task, so the parent's locks are never held across an external call. This does not promise exactly-once external effects: service retries still require the existing service-side idempotency practices. A child can finish immediately or rest on any supported child node, and may itself enqueue another call. A later durable-job incident inside the child leaves the child and parent waiting until existing recovery resolves it. A start failure, such as failed validation, a business-key conflict, or a routing error, rolls back the whole transaction including the child; discard tracked changes and apply section 7 in a new transaction. Infrastructure failures and conflicts use normal job retry. A separate dispatch job is deliberately avoided: it would add a created-but-unrouted child that cancellation, variable edits, and the diagram would all have to handle.

**C. Capture child terminal outcome.** Add one terminal coordinator used by every path that changes instance lifecycle status, including the nine that call `WorkflowRuntimeRepository.SetInstanceStatusAsync` with a terminal status. An end-token event alone is insufficient: act only when the instance is actually terminal, after all required child branches finish. In the child's terminal transaction, read the invocation without locking it. If it is already settled, do nothing more. Otherwise write the result row with the call outcome and mapped output source values, and enqueue one `callResume` on the child's own rows through `jobs.EnqueueAsync`. That job does not count against the parent's open-job cap, so a busy parent cannot roll back a child's completion. Never touch the parent's rows or resume it from this transaction.

A missing or null required output source, a malformed value, or an oversized snapshot must not roll back an otherwise valid child completion. Capture a bounded `call_output_invalid` failure in the result row in the same commit, keeping the child completed. Parent-dependent output validations still run at delivery. A database/infrastructure failure rolls back the whole terminal transaction and is retried through normal recovery.

**D. Deliver the result.** `callResume` takes the parent's locks and fenced token, re-reads the invocation, and loads the result row. If the invocation is settled or the parent is no longer running, complete the job as stale without writing variables or routing. An unsettled invocation whose token or activation no longer matches is an invariant violation, which opens an incident. On success, validate the full frozen output overlay, write one parent variable batch attributed to the call visit, record call completion, settle the invocation, close the visit, record the normal flow occurrence, and run normal parent routing with `forceDurableActivities` in the same transaction. Do not reset the token's automatic-activation count. Respect the mutation tracker's conditional-interruption result before taking the normal flow. On failure, apply section 7.

Infrastructure failures roll back and use existing job lease/retry/incident handling against the same invocation. Authored/contract failures are durable call outcomes, not automatic new-child retries. Resume retries retain the same frozen result. If the boundary error-variable write or downstream parent routing fails, roll back delivery and leave the invocation `running` with its result row for existing operational recovery; do not partially settle the invocation, erase its committed result, or create another child.

**E. Abandon a call and cancel descendants.** `CancelDurableWorkForTokensAsync` is the shared hook: instance cancel, terminate, fault, scoped interrupt, and join cancellation all pass it the tokens they are cancelling. After cancelling those tokens' jobs, it settles their unsettled invocations as `cancelled` and enqueues one `callCancel` on the parent's rows for each invocation with a child, through `jobs.EnqueueAsync` so a parent at its job cap can still be cancelled. A queued invocation has no child, and cancelling its `callStart` is enough. The other direct callers of `CancelOtherJobsByTokenIdsAsync` handle timer and conditional hosts, which cannot be calls in this release; a test pins that.

`callCancel` takes the child's locks, bypassing the fence. If the child is not running, complete the job. Otherwise run the body of `CancelAsync`, separated from its `cancelRoles` check. That passes through the same hook, so the child's own invocations settle and the cascade continues one job per level. The terminal coordinator sees the settled invocation and writes no result. Cancelling the child also cancels its durable jobs, and in-flight REST results are fenced out as they are today. If the job exhausts its retries it opens an incident, and the child stays fenced until an operator retries it.

**F. Job handling.** `ProcessAsync` routes `callResume` and `callCancel` by kind before its token-shape check, and `FinalizeLeasedJobAsync` must never let a call kind fall through to `FinalizeAsyncContinuationAsync`. `EnsureJobFence` and `RequireFencedToken` gain the `callStart` phase and wait. `JobStillOwnsItsWorkAsync` must return true for `callResume` while its invocation is unsettled and for `callCancel` while its child is running; otherwise a conflict completes the job as stale and strands that work with no incident. `RetryIncidentAsync` must exempt those two kinds from its check that the owning instance is running, because their owner may already be terminal. Call jobs use the control queue and `DefaultRetryDelays`, because node job policies are invalid on a call. `BuildJobCreate` snapshots the actor in scope, so pass the fixed actor.

### 7. Failure, cancellation, and audit semantics

Use stable call failure codes, proposed as `call_target_unavailable`, `call_target_incompatible`, `call_start_failed`, `call_input_invalid`, `call_business_key_conflict`, `call_depth_exceeded`, `call_capacity_exceeded`, `call_child_faulted`, `call_child_cancelled`, and `call_output_invalid`. Persist the specific mapping/node/target cause and relevant IDs. Bound recorded descriptions to the engine's 1,000-Unicode-character failure limit and sanitize secrets. Preserve an existing child error-end code as metadata; do not parse exception text to invent a code. Select the boundary with `SelectErrorBoundary` and a status-less `TaskExecutionOutcome`, which can reach only the catch-all.

| Trigger | Required result |
| --- | --- |
| Child normal end or terminate end | Capture success only after the child instance completes; deliver outputs and normal parent continuation. A terminate inside the child cancels its own siblings and queues cancellation of its own descendants, not its parent. |
| Child `errorEndEvent` or other committed terminal fault | Capture `call_child_faulted`; the parent's catch-all boundary handles it or the parent is faulted. This can propagate through nested calls by durable terminal delivery. |
| Child has an unresolved job incident | Keep invocation running and parent waiting. Show child link/operational state; do not mislabel an incident as a completed fault. |
| Operator independently cancels the child | Cancel that child; its descendants follow through `callCancel` jobs. Deliver `call_child_cancelled`; the catch-all boundary handles it or the parent is faulted. Retain normal cancellation authorization. |
| Failure before the child exists: target, start, input, business key, depth, or capacity | No child persists. Route the catch-all boundary, or open a retryable incident on `callStart` and keep the parent waiting. A call blocked before admission remains unreserved; its retry repeats admission and can acquire a slot once capacity is available. An already admitted call retains and reuses its slot, and retries the failed start/target checks without double-counting itself. Rollback restores the prior reservation state; cancelling or otherwise settling the call releases any reservation. |
| Invalid output at capture or delivery | No partial output batch. Route the catch-all boundary, or open an incident on `callResume` and keep the parent waiting. Retrying validates the same frozen result, so it can succeed only when parent state caused the failure; otherwise cancel the parent. |
| Parent cancel/terminate/fault | In the parent's transaction, settle affected calls as cancelled and queue their `callCancel` jobs. The fence stops new descendant transitions from that commit, and the jobs then cancel active descendants, waits, open work, subscriptions, and jobs. Do not feed a cancellation error back into the cancelling parent. |
| Scoped interrupt, join cancellation, or administrative action cancels the hosting token | The same, only for invocations owned by those cancelled token activations. Preserve unrelated parent calls and branches. |
| Parent abandons a call after child terminal commit but before delivery | Keep the child terminal state, suppress output/continuation, and settle the invocation as cancelled. The late resume completes as stale. |

Faulting at a call node is a new path. `FaultInstanceAsync` takes no fault data, and `FaultCode` is set only when a token enters an `errorEndEvent`. Pass the call failure code and description in, and leave the token `faulted` at the call node. Siblings keep the existing `errorEnd` termination and completion reasons, whose wording is "Cancelled when the instance faulted". After a cascade finishes there must be no runnable call job, live descendant task, or late worker result capable of changing the cancelled activation. Cancellation queries operate on indexed unsettled calls and active descendants rather than historical call count.

Add completion reasons `callCompleted` and `callFailed` and termination reason `callFailed`; a boundary catch uses `boundaryCaught`, and an abandoned visit uses the reason of whichever path cancelled it. Add history notes `callQueued`, `callChildStarted`, `callCompleted`, `callFailed`, and `callCancelled`, identifying invocation, parent token/node, child, and resolved version when available. Add the new node type and reasons to the `NodeExecutionQueryService` allow-lists and the `/activity` filters. Keep node execution ledger semantics consistent: active while waiting, completed on success. Attribute parent output changes to its call visit. Child system execution has no fabricated JWT claims; retain original causal audit only where existing durable audit rules authorize it. Do not use automatic call history as a prior human flow-action actor for claim inheritance.

A cascade skips the child's `cancelRoles`, because the decision was authorized on the parent. The child's history records `callActivity` as the performer and names the invocation and parent instance.

### 8. Identity, version changes, reactivation, and limits

**Identity.** Add a dedicated actor factory beside `TimerActor()`, equivalent to `ActorContext("callActivity", [], emptyClaims)`, with no `ActingFor`, `DelegationId`, or `AuditClaims`. Use it for child start, `callResume`, and `callCancel`. Do not reconstruct its authority using `ReadJobActor()` from a job payload. The child's `StartedBy` and system user expression context identify this actor, so a child `assignee` expression or `${sys.user}` default that relies on the starter produces a value no real user owns; the node reference must say so. A business requester/approver is ordinary mapped data, not authorization. The selected start must have empty roles and still passes `EnsureRoleAllowed`. Child user tasks retain their own roles, assignment, inbox visibility, and action checks.

**Direct starts.** An unrestricted start is open to every authenticated user, so a callable fragment can otherwise be started directly with arbitrary inputs, skipping the parent's checks. `StartInstanceCoreAsync` refuses a `callOnly` start unless the call path allows it, and `AllowedStartEvents` in `StartInstance.razor` hides it. Only the default plain start may carry `callOnly: true` in this release; local validation rejects it on non-default starts because no supported route can select them. The flag is optional because some fragments are also useful on their own. It is not caller authorization: any published parent can still call that family.

Reject target starts requiring header-based idempotency in this release. Internal invocation uniqueness supplies retry safety, not an invented HTTP header. Child business-key configuration still applies, including family policy and active/all uniqueness. A conflict fails the call; never attach an unrelated existing instance as its child. Public/manual starts of that child family remain unchanged.

**Version switching.** In preview and locked apply, and in batch compatibility checks, reject changing an instance version when it owns an unsettled outgoing invocation or is the child of an unsettled incoming invocation. Include queued calls and calls with a captured result, not only running children. Persist the caller and child definition IDs independently. After settlement, ordinary compatibility rules govern remaining active work; call history retains its original contracts/versions. Compare the new node/mapping/boundary contracts in `WorkflowVersionCompatibilityEvaluator` wherever snapshots/jobs are evaluated, even though live calls are conservatively blocked. A pending `callCancel` also blocks through `JobDescriptorMatches` until it finishes. Parent version changes never rewrite the child or its saved result.

**Reactivation and administrative overrides.** Reject direct reactivation of a linked child, including completed/cancelled/faulted children whose outputs were already settled. Root reactivation remains subject to existing history/topology restrictions, requires no unsettled calls and no running instance under that root, and creates fresh activation IDs for any future call visit. Pending `callCancel` jobs on the root already appear as `unresolved_durable_work`. Never reopen the old invocation or child. Do not expose force-complete or take-flow actions for a waiting call; existing actions which cancel an enclosing scope pass through the same abandon hook. Variable edits retain existing authorization and cannot change already captured input/output snapshots.

**Limits.** Bind and validate the proposed deployment options on both API and Worker startup, documenting the requirement that replicas use identical values. Check child depth as `parent.CallDepth + 1` when admitting the invocation, which is exact because depth is immutable. Under the entering instance's lock, count only reserved, unsettled invocations across the root before granting a new reservation. Admitted queued and running calls count, including those with a captured result; unreserved queued incidents and settled history do not. Retry uses the persisted reservation rules in sections 4 and 6, so rejected entries cannot consume slots and an admitted call cannot reject itself as an extra admission. Settlement releases its slot in the same parent transaction. Lowering a limit blocks new admissions that exceed it without revoking existing reservations or cancelling work; an already admitted queued call can still create its child. A lower depth setting never changes how existing ancestry is fenced. Limits fail through the call error path. No in-process counter may be authoritative.

Counts must include every reservation already made by the current transaction, including calls entered while routing a newly created child. Flush pending admission/settlement writes before the SQL count or use an equivalent transactional query that sees them; a database count that ignores tracked additions is insufficient. Each transaction may contribute at most `M` newly reserved unsettled calls, where `M = MaxOpenInvocationsPerRoot`.

The setting is a soft admission threshold, not an exact tree-wide quota. Starting at or below `M`, with unchanged limits and at most `C` simultaneously in-flight admission transactions for the root, the conservative bound is `C × M` reserved unsettled calls. `C` includes API and Worker transactions and is not fixed by these options, so there is no fixed fleet-independent overshoot. For a simultaneous burst in which all `C` transactions observe the same count `N < M`, the bound is `N + C × (M − N)`; for `M = 4`, `N = 3`, and `C = 3`, six reservations are possible. Existing reservations above a newly lowered limit drain through settlement; no new admission succeeds while its observed count is at or above that limit. The threshold does not bound blocked incident records or historical visits. Tests must establish own-write accounting and these stated concurrency bounds. An exact quota would require revising the no-new-lock protocol in section 5, not silently assuming serialization.

**Loops.** The hop limit is per transaction and the open-call limit ignores settled calls, so a loop through a child that finishes immediately would otherwise create instances forever. Each call entry counts against `Workflow.Async.MaxConsecutiveAutomaticActivations`, and delivery does not reset the count, even when the child waited on a person. A legitimately long loop stops at the limit with the existing `automatic_loop_limit` incident, and retrying restarts the allowance.

### 9. API contracts and authorization

Existing workflow create/update routes accept the new node schema. Existing instance start/action requests remain unchanged; their response can show a parent waiting at a call before a child ID exists. Do not add a public child-start, complete-call, or retry-call command.

Proposed additive read contract:

| Surface | Change |
| --- | --- |
| `InstanceDetailDto` | Optional `parentCall` summary (invocation ID and state, parent instance/node, root instance, depth) for children, plus bounded call counts. Do not recursively embed child details or every historical invocation. |
| Execution-position projection | For a waiting call, expose invocation ID/state, `capacityReserved`, and nullable child ID so the diagram/detail can distinguish a blocked-before-admission call from admitted queued, running, and result-ready waits. It remains an execution token, not a user task. |
| `GET /api/instances/{id}/calls?page=1&pageSize=50` | New paged history of direct outgoing calls using `PagedResult<CallActivityExecutionDto>`. Use existing page normalization (default 50, cap 200), deterministic invocation-ID descending order, SQL count/page, and bounded batch enrichment. Include visits with no child yet. |
| Call item | Invocation/node/token identity; call state/outcome and `capacityReserved`; child ID/status when present; a bounded, batch-loaded operational summary such as open-incident count, including incidents on the invocation's own jobs found through `CallInvocationId`, because `callResume` belongs to the child and `callCancel` to the parent; target workflow key; resolved child definition ID/version/start event; lifecycle timestamps; safe failure code/description. Never expose captured values, private idempotency keys, credentials, or incident details beyond existing read authorization. |
| `GET /api/workflows/{workflowKey}/callers?candidateId=&deletingId=&page=&pageSize=` | New `CallersPreviewDto` with preview mode (`current`, `candidate`, or `deletion`), effective target ID/version, explicit `noDefault`, nullable `deletionAllowed` and bounded existing deletion blockers, plus `callers: PagedResult<WorkflowCallerDto>`. Each caller identifies its workflow ID/key/version and call node, compatibility, and safe reason codes. Omitting both selectors evaluates the current default; `candidateId` evaluates a published version in the same family; `deletingId` evaluates deleting that family's current default through the shared resolver. Uses the definition routes' existing `Workflow.RequiredRole` filter and existing page normalization. |

The instance route and relation fields follow the current instance-detail read boundary: authenticated actor resolution, `401` when unauthenticated, `404` for a missing instance. Current detail is not filtered by personal-task eligibility; do not promise owner/tenant filtering that does not exist. Relations grant no task, claim, cancellation, role-management, or administrative permission. Any child operation performs its normal authorization check.

The callers route returns `400` for both preview selectors together, an unpublished candidate, or a deletion selector that is not the current default; missing or wrong-family version IDs return `404`. Preview metadata is returned even when the caller page is empty. An allowed deletion with no successor has a null effective target and `noDefault: true`; a blocked deletion has `deletionAllowed: false`, reports its blockers, and evaluates callers against the unchanged current default. It does not claim that compatibility or the selected successor will remain unchanged between preview and command execution.

`POST /api/instances/{id}/cancel` returns when the parent is cancelled; its descendants may remain `Running` until their jobs run. A command on a fenced descendant returns `409`. Document both, because integrations must not read a cancelled parent as a cancelled tree.

Register the routes in `WorkflowInstanceEndpoints` and `WorkflowDefinitionEndpoints`, endpoint/service abstractions, `FlowbitOperationCatalog`, typed `WorkflowApiClient`, DTO documentation, and development OpenAPI. Keep relation projection out of the engine's command orchestration; extend `WorkflowInstanceProjectionService` and a focused relation query/repository path. Add preview blockers and the existing `409` behavior for disallowed version changes/reactivation, with specific reasons. Runtime call failures after a parent command has committed appear as call/instance state, not a retroactive HTTP error to that command.

General list/search semantics, cursors, and counts stay unchanged: parents and children are ordinary independent entries. Use the detail links and calls route to navigate relationships. Do not add partially implemented GET-only relationship filters.

### 10. Shared editor and Flowbit.Ui

Keep implementation in the canonical assets under `Flowbit.Ui/wwwroot/editor/` and shared Razor components; do not fork the standalone, hosted, or viewer editor.

- Add a palette/type option and a thick-border task-shaped node with an accessible call-activity label. Reuse task geometry for sizing, edge attachment, hit targets, lane assignment, and boundary placement. Update `editor-template.js`, `flowbit-editor.css`, and `flowbit-editor.js` together.
- Add inspector fields for workflow key, the fixed published-default binding explanation, and typed input/output mapping rows. Reuse `attachInspectorHelp()` and existing accessible label/history helpers. Parent variable selectors can use local declarations; child names are entered explicitly. Hosted target discovery is not required for this release, and the standalone editor must remain usable without network access.
- Add a `callOnly` checkbox to the start-event inspector, with help explaining that it hides the start from people and API callers and is valid only on the default plain start. Keep an invalid imported value visible with an actionable error; changing the default must not silently clear the previous start's flag.
- Explain that the server checks the child contract when publishing/starting, that a Worker is required, and that inputs/results are snapshots. Do not display a resolved version in the authored JSON: it belongs to an invocation.
- Support add/remove mapping rows, validation messages, the one error-boundary action, delete, lane movement, type changes, undo/redo, import/export, and file/API save through the shared export pipeline. Mapping rows need no reordering, because a batch has no order. Reject unsupported boundary kinds and async/MI properties consistently in all hosts.
- Update viewer rendering, `ExecutionPositionText`, and execution overlays so a waiting call looks active and can be selected, without exposing authoring controls. Keep per-mount state, read-only shortcuts, and existing zoom/scroll behavior.
- Add a focused `InstanceDetails` component for parent/root links and paged outgoing calls, linked from `InstanceDetail.razor`. Show queued/no-child, running, result-ready, successful, failed, and cancelled states, with target key/version and failure explanation. Distinguish a call blocked before admission from an admitted queue entry, and an operationally blocked child from a terminal fault. Tell the reader when a child's parent call was cancelled and its cancellation is still in progress. Link into child detail and back to the exact parent; repeated calls to the same node remain distinct rows.
- Show incompatible callers in the Workflows page's confirmation before set-default and before deleting a default. Use `candidateId` for set-default and `deletingId` for deletion; display the resolved successor, no-default warning, or deletion blockers from the response, including when there are no callers. State that the preview reflects current state and the command rechecks it. Let the AI panel select call targets beside shared variables.
- Reuse current page refresh behavior; do not introduce a new polling/notification subsystem. Preserve loading/error/empty states, pagination on refresh, keyboard navigation, and layout at desktop/tablet/phone widths.

### 11. Implementation sequence and review gates

Each step includes its relevant tests and affected documentation. The item remains In progress until the full acceptance matrix is satisfied; intermediate schema or engine support is not a shippable call feature.

| Step | Deliverable | Exit gate |
| --- | --- | --- |
| 1. Contracts and fixtures | Shared call types, default-start-only `callOnly`, strict JSON handling, local/readiness checks, proposed DTOs, representative parent/child test fixtures, failure/state vocabulary. | Old documents and characterization arrays unchanged; all new invalid shapes fail explicitly; target contract tests cover defaults/roles/idempotency/business keys; both new features obey the publication gate. |
| 2. Persistence and job handling | Migration, entity/repository constraints, capacity reservations, job kinds and `CallInvocationId`, `callStart` wait, complete-ancestry fence, stale-classification and incident-retry changes. | Migration/model tests pass; lowered depth cannot weaken the fence; malformed ancestry fails closed; existing non-call behavior and query counts remain intact. |
| 3. Creation | Call entry, `callStart` through the job-owned start path, pinned target, fixed actor, admission/retry rules, loop guard, one-child uniqueness. | Worker restarts/duplicate execution cannot create an orphan or duplicate; rejected admissions consume no slots; reserved retries do not double-count; own-write/concurrency bounds pass; cancelled queued calls cannot start a child; a looping call stops at the guard. |
| 4. Terminal delivery and cancellation | Central terminal coordinator, result rows, `callResume`, output batching, boundary/fault/incident handling, abandon hook, `callCancel` cascade. | Success/failure/race matrix passes, including immediate completion, nested calls, shared-variable work, and a parent cancel during a child's synchronous REST call. |
| 5. Compatibility and operations | Version/reactivation/admin blockers, incident recovery, retention/deletion guards, caller references and candidate/deletion preview, configuration/diagnostics. | Single and batch paths agree under races; preview and deletion share successor/blocker rules; no supported operation bypasses the invocation fence. |
| 6. Read APIs and UI | Relation projection, paged routes, OpenAPI/client, shared editor authoring/viewer, instance navigation, caller warnings, AI target selection. | API/component tests and standalone/hosted browser scenarios pass with console/layout evidence. |
| 7. Release evidence | Canonical examples, all owning guides, deployment sequence, full regression and recovery runs. | Check every acceptance criterion against implementation/evidence; mark Done only after resolving reported limitations. |

Build the fence and job handling before calls can be published. Keep the durable publication gate closed for both `callActivity` and `callOnly` in incomplete deployments, with the authoring freeze in section 13 covering legacy replicas. Use a single internal ownership model; do not ship an interim inline implementation and replace it later.

### 12. Tests and real-browser verification

Extend existing test families where they own the behavior, and add focused call-contract, invocation-persistence, lifecycle, and concurrency suites. PostgreSQL-backed tests must exercise real transactions/constraints and multiple service scopes; in-memory repository tests cannot establish lock/fence correctness.

| Area | Required cases |
| --- | --- |
| JSON/model/loader | Old definition round trip; default binding omission; strict wrong-type/unknown-member rejection; missing config; duplicate targets; null versus missing; 100-entry bound; wrong-node fields; direct self-call; `callOnly` placement and omission; non-default call-only start rejected; changing `initialEventId` leaves an explicit error until the old flag is corrected; legacy loader preservation; malformed import keeps current document safe. |
| Mapping contracts | Start, flow, REST-target, and process-variable sources; every supported scalar/array type; mismatched declarations/nullability; required sources reject missing and null; optional sources omit missing and pass null; child defaults and complete-overlay validation; no shared/reserved/undeclared destinations; output error-target collision; size limit; validation failure writes nothing. |
| Target/version/identity | Missing/unpublished/no-default family; default changes while queued; exact pin after creation; protected or idempotency-configured start rejected; `callOnly` refused by public routes and start page, allowed from a call; fixed child system actor and no JWT/delegation transfer; parent continuation runs as `callActivity` after a person or message client finished the child; child task permissions unchanged; business-key conflicts never attach another instance; caller list and candidate check. |
| Publication/rollout | Gate rejects both call nodes and otherwise synchronous `callOnly` targets through create/update-with-publish, publish, set-default, and deletion-driven promotion; drafts remain allowed on upgraded replicas; gated successor is not silently skipped; rollout rehearsal verifies the new-configuration authoring freeze until all replicas/assets and open authoring tabs are upgraded/refreshed, followed by Worker health and option checks before publication is enabled. |
| Caller-impact preview | Current/candidate/deletion modes; conflicting selectors and invalid/missing/wrong-family IDs; actual business-key/distributor-filtered successor; incompatible successor; no successor marks every caller unavailable; existing deletion blockers leave the current default unchanged; preview metadata survives an empty page; a changed default/successor between preview and command is rechecked by the command. |
| Normal lifecycle | Immediate child end; child human/message/timer/conditional wait; parallel child branches; nested calls; loop revisit makes a new child; parent parallel branches continue; child terminate ends only child scope; exactly one parent traversal/output batch. |
| Faults/operations | Error end caught/uncaught; independent child cancel; failure before the child exists, with a boundary and as an incident whose retry succeeds after the cause is fixed; missing/null/invalid/oversized output capture preserves completed child and fails the call; job incident remains waiting; retry uses same invocation; conflicts on `callResume` and `callCancel` open incidents rather than completing as stale; incident retry works when the job's owner is terminal; failures in boundary/continuation use existing recovery without losing child result. |
| Cancellation/races | Parent cancel/terminate/fault versus child creation, child transition, completion, and result delivery; parent cancel returns while a child's synchronous REST call is in flight; the fence returns `409` to commands at every depth and completes a fenced job as stale; create a depth-8 tree, lower `MaxDepth` to 2, cancel the root, and verify the deepest descendant remains fenced; missing/cyclic/truncated/root-or-depth-inconsistent ancestry blocks mutation and opens a job incident rather than completing stale; a transition already in progress commits and its result is discarded; Worker stopped keeps tasks visible and rejects actions, and the cascade finishes after restart; scoped interrupt/join/admin cancellation; completed child stays completed; stale jobs cannot write; unrelated siblings survive scoped cancellation; no deadlocks under concurrent parent, child, and shared-variable writers. |
| Crash/replay | Crash before/after each durable commit; duplicate leases/expired lease finalization; two workers claim same logical work; child result committed but resume delayed; `callCancel` delayed or retried; transient resume failure; uniqueness and all-or-nothing assertions after recovery. |
| Admission/limits | Depth 0/limit/limit+1; staged mutual recursion stops at the limit; a loop with an immediate child stops at the automatic-activation guard and continues after incident retry; saturated root plus several sequential rejected calls leaves the reserved count unchanged; freeing one slot lets an unreserved retry acquire it; at limit 1 an already-reserved queued call retries without needing a second slot; same-transaction parallel/new-child entries include all own reservations; simultaneous `M=4`, `N=3`, `C=3` burst may reach 6 and stays within the section 8 bounds; rollback and every settlement path preserve/release reservations correctly; lowering limits preserves admitted work and prevents excess new admissions. |
| Compatibility | A parent at its job cap can finish calls and be cancelled; parent and child version preview/apply/batch blockers include unreserved incidents; switch race with call entry; child reactivation denied; root reactivation rejected while a descendant runs or an unreserved call remains and creates fresh visits only. |
| Persistence/retention | Fresh migration and upgrade; FKs/checks/indexes/unique keys, including reservation/state consistency and persisted depth 0–32; old roots; node/history pruning; terminal child output waiting for delivery; invocation tombstone retention; definition deletion protected; down migration refuses live call data. |
| API/projection/security | Authenticated relation reads, missing IDs/401; safe DTOs; `resultReady` projection; deterministic page/count; repeated visits; no N+1 reads; nullable child/version before creation; permissions on child mutations; caller route authorization; operation catalog/OpenAPI coverage and unchanged ordinary search. |
| Editor/UI | New type and shape; mapping editing; boundary attachment; move/connect/delete; type-switch confirmation and undo/redo; export/load; `callOnly` checkbox and default-switch validation without silently losing flags; accessible labels; viewer selection; paged parent/child navigation; admission-blocked and cancellation-in-progress messages; caller confirmation with successor/no-default/blocker states; states/errors/empty lists at relevant widths. |

Relevant existing tests include `DefinitionValidationTests`, `EditorValidatorCharacterizationTests`, `EditorErrorBoundaryMatchTests`, `EditorViewerModeTests`, `DurableJobRepositoryTests`, `DurableJobConflictTests`, `WorkflowVersionCompatibilityEvaluatorTests`, `WorkflowInstanceDetailEndpointTests`, `OpenApiContractTests`, and `InstanceDetailDisplayComponentTests`. Preserve their existing contracts rather than replacing them with call-only fixtures. New examples must update the hard-coded count in `ExampleWorkflowCatalogTests` and use its `example-` ids, and browser scenario E18 needs the new type in `editor-inspector-census.json`.

Run the full solution tests after targeted suites. These repository-root commands work unchanged in PowerShell and Bash:

```text
dotnet test Flowbit/Flowbit.slnx --nologo --verbosity quiet
git diff --check
```

Compare `Flowbit/tools/InstanceLoadTest` results before and after, to confirm instances outside a call tree are unaffected.

For real-browser work, follow the build/publish/prerequisite commands in the [smoke suite](../../Flowbit/tests/Flowbit.BrowserTests/README.md#commands) and [Worker acceptance suite](../../Flowbit/tests/Flowbit.BrowserAcceptanceTests/README.md#setup-and-commands), then run both suites:

```text
dotnet test Flowbit/tests/Flowbit.BrowserTests/Flowbit.BrowserTests.csproj -c Release --no-build --no-restore
dotnet test Flowbit/tests/Flowbit.BrowserAcceptanceTests/Flowbit.BrowserAcceptanceTests.csproj -c Release --no-build --no-restore
```

Keep the ordinary smoke fixture Worker-free: it can exercise standalone/hosted draft authoring and rejection by the disabled durable-publication gate. Put publish/start/parent-child completion and recovery scenarios in the existing Worker-enabled acceptance project, outside both solutions and existing CI jobs. Set up examples through authenticated HTTP; use real API response gates and fixture-owned Worker restarts, not database edits or injected application state.

Manual verification must exercise real clicks, typing, keyboard input, connections/node drags, save/load (including native picker success/cancel), mapping edits, boundary placement, type changes and undo, and parent/child detail navigation. Inspect editor layouts at 1440×900 and 1024×768 and runtime detail at those sizes plus 390×844. Record browser/version, actual localhost URLs, viewport sizes, interactions, console warnings/errors, and screenshots of the new node/inspector and relationship panel. Automated Chromium checks complement these scenario-specific checks; Jint/DOM stubs do not replace them.

These were the implementation gates. [Implementation evidence](#implementation-evidence) records the result.

### 13. Documentation, examples, and rollout

Current-behavior documentation shipped with the implementation.

| Owner | Required implementation documentation |
| --- | --- |
| [Node reference](../node-reference.md), [BPMN support](../bpmn-support.md) | Call JSON/topology, default-start-only `callOnly` and explicit default-switch correction, thick-border shape, mapping/identity/terminal semantics, what `sys.user` means in a child, catch-all boundary support, intrinsic durability, and first-release limitations. |
| [Developer guide](../developer-guide.md), [API reference](../api-guide.md) | HTTP-only paired workflow setup, default resolution timing, idempotency/business-key distinction, outputs/failure/recovery, reservation-aware relation DTOs/routes, caller current/candidate/deletion preview and its advisory snapshot semantics, current read authorization, compatibility blockers, and that a cancelled parent does not yet mean a cancelled tree. |
| [UI guide](../ui-guide.md) | Inspector fields/help, server-only target validation, parent/child links and paged visits, waiting versus incident/fault states, cancellation in progress, and caller warnings. |
| [AI authoring](../ai-authoring.md) | Call target selection and what the assistant is given about each target. |
| [Database schema](../database-schema.md) | Every new column/table/FK/check/index/job kind and retained invocation identity; include capacity reservation/state checks, hard persisted depth bound, and optional prunable audit references. |
| [Deployment](../deployment.md) | API/Worker options, admission reservations and concurrency-dependent soft bounds, effects of lowering limits, complete-ancestry fencing, migration/replica ordering and new-configuration authoring freeze, publication gate for both calls and call-only starts, descendant cancellation lag and its dependence on running Workers, incidents, retention, and rollback restrictions. |
| [Runtime reference](../../Flowbit/README.md), [AGENTS.md](../../AGENTS.md) | The one-instance-per-transaction rule, invocation and result row ownership, admission/retry accounting, complete-ancestry fence and invariant failures, durable call phases, terminal coordinator, version/reactivation rules, and retained identity invariants. |
| [Example catalog](../../examples/README.md) and paired examples | A parent approval call and its child, declared inputs/results, unrestricted child start, publish child/default before parent, Worker prerequisite, expected terminal states, and boundary-failure variant. Use synthetic data and no provider calls. |
| [Root README](../../README.md), [portable authoring guide](../../authoring/flowbit-authoring/references/authoring-guide.md) | Implemented capability and supported JSON/geometry. Replace screenshots only when the change makes them misleading; update authoring knowledge/schema/example checks. |
| Browser suite READMEs, this page, [backlog](README.md) | New coverage/evidence and actual limitations; mark Done only on completion and track still-wanted deferred work as separate items. |

Roll out in order: back up; freeze API/UI authoring, import, publication, and default selection of definitions using either `callActivity` or `callOnly: true`; set `WorkflowDurableProcessing:PublicationEnabled` to false, because the checked-in value is true; apply the additive migration; upgrade all API and Worker replicas and matching editor assets; refresh existing authoring tabs so no old editor can strip the new fields; verify Worker health, consistent call options, and both publication-gate cases; then lift the authoring freeze, allow durable publication, publish/default the child, and publish/start the parent. Enforce the freeze operationally until all replicas are upgraded: old APIs can ignore or strip `callOnly` before validation, so the new gate alone cannot protect requests routed to them. Do not expose or use the new authoring controls against a mixed fleet.

On upgraded replicas the gate covers call-only targets even if they are otherwise synchronous, as well as call nodes, and applies to every publication/default-selection path listed in section 3. It also holds back other durable definitions during rollout, while already-published work keeps running. Mixed call-aware and legacy replicas are unsupported once either a call node or `callOnly: true` has been published: a legacy API ignores the start restriction, fence, and abandon hook, and a legacy Worker treats an unknown job kind as an async continuation. Existing definitions without these features remain valid.

Use metadata-only diagnostics for invocation/root/child IDs, state changes, queue age, failure codes, the age of open `callCancel` jobs, and fenced children still running. Do not log mapped values or credential-bearing child errors. Demonstrate recovery with the Worker stopped/restarted, a queued call cancelled before start, a result awaiting delivery, and a parent cancelled while the Worker is stopped. Cancellation does not compensate external effects; document that limitation in the example and operations guide.

## Implementation evidence

Recorded on 2026-11-01 on the working tree before commit. Commands ran from the repository root, with one suite running at a time.

| Check | Result |
| --- | --- |
| `dotnet test Flowbit/Flowbit.slnx --nologo --verbosity quiet` | 3,800 of 3,800 passed. |
| `dotnet test Flowbit/tests/Flowbit.BrowserTests/Flowbit.BrowserTests.csproj -c Release --no-build --no-restore` | 102 of 102 passed in Chromium, including E19 and H13 at 1440×900 and 1024×768, E18 and E20 at 1440×900, and H6 selecting a call target. Scenarios fail on a console error or an unexpected dialog. |
| `dotnet test Flowbit/tests/Flowbit.BrowserAcceptanceTests/Flowbit.BrowserAcceptanceTests.csproj -c Release --no-build --no-restore` | 93 of 93 passed with a real Worker, covering parent and child links at 1440×900, 1024×768, and 390×844, a rejection with and without a boundary, a parent cancelled while the Worker is stopped at 1440×900 and 390×844, an incident whose retry starts the child, the call-only start page, and the set-default and deletion confirmations at 1440×900 and 390×844. |
| `git diff --check` | No problems. |
| Relative links, anchors, and JSON fences in the changed pages | 1,969 links and anchors resolve, apart from 5 package-relative paths in the portable authoring guide that resolve inside the generated skill, and all 45 JSON fences parse. |

An earlier full unit run failed 1 of 3,776 on `WorkerDispatcherTests.CancelsLocalInvocationOnShortFenceCheckCadence`, a 750 ms timing bound with no call in it. It passed three of three alone, and in the run above.

Instances outside a call tree are unaffected. One start issues the same 37 SQL commands in the same order on both builds, with two more columns on the instance row. `Flowbit/tools/InstanceLoadTest` gave these instances per second:

| Run | Before (`0c92ce5`) | This change |
| --- | --- | --- |
| Instance resting on a task, 5,000 per run | 61.9, 62.1, 62.5, 61.3, 61.4, 59.3 | 61.6, 60.7, 61.6, 60.7, 61.0, 60.9 |
| Instance finishing at once, 3,000 per run, both builds on one disk | 55.2, 56.9, 53.4, 57.0, 57.7, 56.6 | 56.6, 56.9, 56.7, 56.6, 57.2, 56.9 |

A first comparison of the second run showed about 4% less. That came from the published hosts sitting on different disks, not from the code.

### Rollout rehearsal

[Call activity rollout](../deployment.md#call-activity-rollout) was run on real API and Worker hosts against one PostgreSQL 17 database, through an ad hoc script that used only HTTP and read-only SQL. All 41 checks passed.

- **Upgrade.** `0c92ce5` binaries left an ordinary instance waiting on a task. The database was backed up with `pg_dump`, and the hosts were replaced with this change's binaries and the gate set to false. The migration ran, existing instances kept root defaults, no call history appeared, and the waiting instance finished on the new API.
- **Gate.** With the Worker healthy, publishing a call node, publishing a call-only start, and publishing a draft of one were each refused, naming `PublicationEnabled`. Drafts of both saved, and a definition with neither feature published.
- **Options.** `WorkflowCallActivities__MaxDepth=99` stopped the API at startup, and `WorkflowCallActivities__MaxOpenInvocationsPerRoot=0` stopped the Worker, each with a message naming the setting and its range.
- **Order.** After the gate was set to true and the API restarted, a parent published ahead of its child was refused. The child and then the parent published, and the first call ran to delivery.
- **Recovery with the Worker stopped.** A call entered while stopped stayed queued with a reservation and no child, and started one child after a restart. A queued call cancelled while stopped settled with no child and never got one. A child finished while stopped left its result ready and the parent waiting, and a restart delivered it once. A parent cancelled while stopped returned at once, its child stayed running and returned `409` to an action, and a restart cancelled the child. No runnable call job or open incident remained.

The walkthrough in the [call activity examples](../../examples/call-activities/README.md) also ran against a running stack and reached a running call with its child. PowerShell 7 is not installed here, so its commands ran in Windows PowerShell 5.1 with the token in a variable in place of `Read-Host -MaskInput`. `jq` is not installed either, so the Bash commands ran with a small local stand-in for their five `jq` filters, and `jq` itself was not exercised.

### Hands-on browser checks

The built-in browser of the Claude desktop app (Chrome 152.0.7977.167), against a private stack on loopback: the standalone editor at `http://127.0.0.1:15160/flowbit-editor.html`, and Flowbit.Ui at `http://localhost:15152` (`/token`, `/instances/1`, `/instances/2`, `/workflows`).

- **Standalone editor, 1440×900 and 1024×768.** Built a parent by hand: added nodes, dragged them apart, and connected them by dragging handles, then chose **Call Activity** on a task, which gave the thick border and the Inputs, Results, and Error boundary sections. **+ Add mapping** moved focus to its first field and Tab moved through the row. Typing a declared source adopted its type and offered it as a suggestion. **+ Add error boundary event** attached the boundary and entered connect mode. Save named each missing key, undeclared source, and undeclared target. Changing the type away removed the boundary, and Ctrl+Z and Ctrl+Y restored and removed it with the mappings intact. **Call only** kept focus, and clearing the default start then failed save with its message. The inspector stayed inside its panel at both sizes, including with the section help open at 1024×768.
- **Runtime.** On a parent and child from the examples, the waiting position, the diagram's selected-node panel, the child link, **Called by**, and the root and depth all read correctly. Approving the child delivered `managerApproved` and moved the parent on, and the child's **Call state** read **Result ready**, then **Completed** after **Refresh**. Instance detail was read at 1440×900 and 390×844, where the calls table scrolls inside its panel with no page overflow, and the suite covers 1024×768. **Set default** on `/workflows` showed its callers at 1024×768, and the suite covers 1440×900 and 390×844.
- **Console.** No errors or warnings in either tab, only Blazor's connection messages.

Screenshots are in the git-ignored run folders: `artifacts/browser/runs/*/e19-call-activity-*/call-activity-inspector.png` and `h13-hosted-call-activity-*/hosted-call-activity.png`, and `artifacts/acceptance/runs/*/call-parent-child-*/`, `call-cancel-*/`, and `call-callers-*/`.

### Found during acceptance

All are fixed, and the product fixes have permanent tests.

- **Page state across instances.** A link between a parent and a child reused the same page component, with the previous instance's state and polling. `InstanceDetailRoute.razor` now keys the page by instance.
- **Drawing wrote to the document.** Selecting an imported call whose mapping list was null replaced it with an empty list, so the invalid value vanished without an edit or a history entry. Drawing now writes nothing and save reports the null.
- **Selected call border.** The selection stroke replaced the call's thick border, so a selected call looked like any task.
- **Order-dependent suites.** Call tests left runnable jobs behind, and suites sharing the database then leased those jobs and failed depending on test order. Each call test class now retires its own.
- **Required cases with no test.** Comparing [section 12](#12-tests-and-real-browser-verification) with the test names before this record found some: a child waiting on a message, a timer, or a condition; parallel child branches; a child's own job incident; a parent fault on another branch; an administrative action ending a call; a conflict on `callCancel`; a lowered open-call limit; direct and batch version changes; command counts on the calls route and detail; ordinary instance search; business-key and distributor-filtered successors; and a successor that changes after its preview. They are now in `CallActivityScenarioTests` and the admission and operations suites. All passed on their first run, so they changed no product code.

### Decisions the plan left open

- The AI panel selects at most 20 call targets for a turn.
- `/activity` takes node types and completion reasons as free text, so only the server's allow-lists changed.
- The editor has no call entry of its own in the palette. As with every other type, **Add Node** and then **Type** creates one.
- The `Flowbit.Runtime.CallActivities` meter was left off the Worker's Prometheus endpoint at first. The [review below](#review-after-the-commit) added it, and [Signals and actions](../deployment.md#signals-and-actions) names its metrics.

**Not verified:**

- The native file picker's success and cancel paths, which need a person. In the desktop app's browser, **Save JSON** on a valid document showed no dialog and changed nothing, and saves in the suites use the download fallback. `save()` did not change in this work.
- A mixed fleet of call-aware and legacy replicas, which is unsupported. The freeze before an upgrade and refreshing open authoring tabs are operational steps that no test here can enforce.
- A real AI provider. Call target selection ran against mocked HTTP, because billed runs need explicit authorization.

**Noticed, not changed.** Neither comes from this work. The browser tab's title goes stale after any in-app navigation, on every page, because `<HeadOutlet />` in `App.razor` has no render mode. The connect-mode hint's first letters sit under the diagram tools button when connect mode opens with that button collapsed.

**Later releases** stay listed on this page. No separate backlog items were filed for them; [TASK-05](task-05-multi-instance-automatic-activities.md) already covers multi-instance calls and [EVENT-07](event-07-compensation.md) compensation.

### Review after the commit

A review of commit `99ee2c6` against this page found the problems below. All are fixed, and the product fixes have permanent tests. Recorded on 2026-11-02 on the working tree before commit.

- **Deleting a default draft.** A family's first version is its default even as a draft, and the Workflows page previews every default's deletion. The callers route accepted only the published default, so the page could not delete that draft. It now checks the version's own flag, and a draft with a published successor previews that successor.
- **Failure text.** Job and call failure text was cut by UTF-16 length. A cut inside a surrogate pair made the database driver refuse the write, which wedged the call. A boundary's diagnostic could also pass its limit with the code in front. `UnicodeText.Limit` now counts characters wherever this text is stored.
- **Nested incidents.** A call's `openIncidentCount` skipped incidents on the jobs of the child's own calls, so a parent showed no sign of them.
- **Cancellation notice at depth.** Only an instance whose own call was cancelled showed the notice, so a deeper descendant looked healthy while refusing commands. `parentCall.cancellationInProgress` now reads the ancestry, and the page keeps refreshing until the instance stops.
- **Incidents on call messages.** **Cancel instance** on a `callResume` incident targeted the child that sent the result, which had usually finished. `callTargetInstanceId` now names the instance the message acts on, and the row's link and button use it. Such an incident also stayed open forever once its work was gone, so abandoning a call now closes its `callResume` incident and a child's terminal commit closes its `callCancel` incident.
- **Imported settings.** Drawing removed most settings a call does not support, on the node and on its outgoing flow, and a second outgoing flow, so an import lost them without a word. They now stay and fail save, and **Remove these options** or a type change clears them.
- **Telemetry.** `callCompleted` was never counted, delivery wrote no log, and the call meter was not on the Worker's `/metrics`.
- **Heavy reads.** The calls route, instance detail, and the abandon path loaded every captured snapshot, each up to a mebibyte, and discarded it.
- **Stale documents.** The AI guide and the example catalog still said 21 node types and 55 definitions, and the catalog credited call coverage to the wrong suite.
- **Cases with no test.** A scoped interrupt raised by a call's own outputs during delivery, `FlowInfo` read after a call, and an inclusive join waiting for a branch resting on a call had no test. They are now in `CallActivityScenarioTests`, and all three behaved correctly without a product change.

| Check | Result |
| --- | --- |
| `dotnet test Flowbit/Flowbit.slnx --nologo --verbosity quiet` | 3,877 of 3,877 passed. Two Worker dispatcher cases added after that run passed with their class, 12 of 12; only test code changed. |
| `dotnet test Flowbit/tests/Flowbit.BrowserTests/Flowbit.BrowserTests.csproj -c Release --no-build --no-restore` | 102 of 102 passed in Chromium. E20 now also imports roles, a due date, and a flow condition, and clears them through the node's and the flow's inspector. |
| `dotnet test Flowbit/tests/Flowbit.BrowserAcceptanceTests/Flowbit.BrowserAcceptanceTests.csproj -c Release --no-build --no-restore` | 98 of 98 passed with a real Worker. New: the notice on a deepest instance and an incident cancelling the parent it acts on, each at 1440×900 and 390×844, and a default draft deleted through its dialog at 1440×900. |
| `git diff --check` | No problems. |

**Hands-on checks.** The built-in browser of the Claude desktop app, at its natural 1024×768 and an emulated 375×812, against a private stack on loopback with real API, UI, and Worker hosts and a PostgreSQL 17 container.

- **`/operations`.** The refused result's row showed **Acts on instance #3** and **Cancel instance #3**. Clicking it cancelled the parent, closed the incident, and left the child **Completed**.
- **`/instances/7`, at depth 2, with the Worker stopped.** The notice appeared while **Call state** read **Running**, an action returned `409`, and the page had no horizontal overflow at either width. After a Worker restart the page changed to **Cancelled** and dropped the notice with no reload. The root's cancel went over HTTP because this browser suppresses the page's native confirmation.
- **`/workflows`.** A default draft's dialog warned that no published default would remain, and **Delete version** removed it.
- **`/editor/flowbit-editor.html`.** An imported call with roles, a due date, `requiresClaim`, and a service, whose flow had a condition and roles, showed both warnings and failed save with each reason. **Remove these options** cleared each one, undo and redo moved the node's removal as one step, and save then raised no dialog. The file went in through the page's file input by script, because this browser cannot drive the native picker.
- **Worker `/metrics`.** It reported `flowbit_call_events_total{event="callCompleted",failure_code="none"} 1` after one delivered call, and `flowbit_call_cancel_age_seconds_count 2` once the restarted Worker had stopped both descendants.
- **Console.** No errors on any page, only Blazor's connection messages and this browser's note about the suppressed confirmation.

### Second review

A second review of the same working tree raised four more problems. Each was reproduced before any change, against a control that behaved correctly. All are fixed with permanent tests. Recorded on 2026-11-02 on the working tree before commit.

- **Shared aliases in a target's rule.** Output validation read only the instance's own values, so a rule such as `total <= approvalLimit` could never pass when `approvalLimit` was a shared alias, which publishing accepts. With no boundary the call stayed at **Result ready** with `call_output_invalid`. With a boundary, a valid result took the error path and wrote nothing. Validation now reads the effective values, and the access plan lists the read. It is not locked, like every other shared read, so the lock-order proof is unchanged.
- **Caught entries and the guard.** An entry that failed before admission and was caught by its boundary returned before the count, so two such entries at a limit of 1 both ran and left the count at 0. This did not allow a runaway: a tight loop stops at the per-transaction hop limit, and any durable step in a loop counts. It did break the documented rule, so these entries now count. At the limit the entry opens the guard's incident instead of taking its boundary, and retrying it reaches the boundary from `callStart`.
- **The page after an action.** Claim, unclaim, and taking a flow reloaded only the actions and the diagram, and a single-token instance is not polled. A call entered by the action showed **Loading calls…** or a stale list until **Refresh**, whether the instance ended or kept running. **Administrative actions** also kept offering the finished task, which predates calls. `RefreshFrom` now reloads through `RefreshInstanceStateAsync`. Reactivation and version change still skip that panel; they cannot change the call list, so they were left as they are.
- **Malformed imports.** The earlier fix kept only values of the right type, so the loader still repaired the rest: `callOnly: null` vanished, a flow's `completionPriority: 1.5` became null, and `isSelectable: "false"` became true. Others, such as `asyncBefore: 0`, stayed but passed save and reached the server. Comparing 52 settings with 14 JSON values each found 192 disagreements in 728 cases. A call and its outgoing flow now keep every value as authored, save names one of the wrong type, and a non-list `variables`, which too much of the editor walks, stops the import with a message. `callOnly` stays on any node, with a way to remove it. Other node types keep their tolerant loading, as the [load rule](../../AGENTS.md#key-conventions--invariants) says.

| Check | Result |
| --- | --- |
| `dotnet test Flowbit/Flowbit.slnx --nologo --verbosity quiet` | 3,921 of 3,921 passed. New: all four points, including `Import_AgreesWithTheServerOnEveryValueOfACallsOtherSettings`, which compares the editor and the server over all 728 cases. |
| `dotnet test Flowbit/tests/Flowbit.BrowserTests/Flowbit.BrowserTests.csproj -c Release --no-build --no-restore` | 102 of 102 passed in Chromium. E20 now also imports `requiresClaim: 0`, `completionPriority: 1.5`, `isSelectable: "false"`, and `callOnly: null` on a start and an end event, and clears them through each inspector. |
| `dotnet test Flowbit/tests/Flowbit.BrowserAcceptanceTests/Flowbit.BrowserAcceptanceTests.csproj -c Release --no-build --no-restore` | 101 of 101 passed with a real Worker. New: a task action that ends in a failed call, at 1440×900 and 390×844, and a later call appearing beside an earlier one on a running instance, at 1440×900. |
| `git diff --check` | No problems. |

**Hands-on checks.** The built-in browser of the Claude desktop app, at its natural 1024×768 and an emulated 375×812, against the canonical editor files on loopback and then a private stack with real API and UI hosts and a PostgreSQL 17 container. Files went in through the page's file input by script, because this browser cannot drive the native picker.

- **Standalone editor.** A call with `requiresClaim: 0`, `roles: 5`, `assignee: 5`, `asyncBefore: null`, `multiInstance: false`, and `claimMode: 5`, a flow with `completionPriority: 1.5`, `isSelectable: "false"`, `isDefault: 0`, `condition: 5`, and `roles: 5`, and `callOnly: null` on the start and end, redrew without error and failed save with 16 reasons that named them. **Remove this value** and **Remove these options** cleared each one by real clicks, and save then raised no dialog. Undo and redo moved the flow's removal as one step. `variables: 0` on the call and `variables: "note"` on its flow were refused with a message, and the current document stayed. At 375×812 the warning fit the inspector with no horizontal overflow.
- **`/workflows/new`, hosted.** **Save new version** on a malformed document listed its reasons and stayed on the page. This check found the warning touching **+ Add attribute**, because Bootstrap's reset removes a paragraph's top margin. The node's and the flow's warnings now set their own, and the gap measured 12 pixels afterwards.
- **`/instances/1`, hosted.** One click on **Take action** entered a call that failed on entry. Within a second the page showed **Completed**, a **Failed** row with `call_input_invalid`, no **Loading calls…**, **Administrative actions** with no positions left, and **Reactivate instance**, with no refresh.
- **Console.** No errors on any page, only Blazor's connection messages.

The Status above and the items under **Not verified** are unchanged by this review.
