# TASK-05B: Multi-instance REST service activities

[Parent TASK-05](task-05-multi-instance-automatic-activities.md) · [Shared core TASK-05A](task-05a-multi-instance-core.md) · [Enhancement backlog](README.md) · [Documentation home](../index.md)

| Field | Value |
| --- | --- |
| Status | In progress |
| Category | New task type |
| Effort | M |
| Priority | Not ranked |
| Parent | [TASK-05](task-05-multi-instance-automatic-activities.md) |
| Depends on | [TASK-05A](task-05a-multi-instance-core.md) |
| Related | [EVENT-05](event-05-error-code-boundaries.md) |

## Summary

Implement the REST `serviceTask` adapter for automatic multi-instance execution: process an instance or shared typed array in parallel or sequentially, invoke one durable HTTP operation per admitted item, and collect validated local outputs in the ordered aggregate owned by TASK-05A. The first release includes shared reads/writes and interrupting or non-interrupting timer/conditional boundaries on the body.

The REST adapter is implemented behind the Development-only release switch, including the review fixes recorded below. TASK-05B remains **In progress** until the [remaining acceptance and handoff work](#still-open) is complete; see the [implementation record](#implementation-record-2026-11-12) for current behavior and verification. The original plan below records the pre-implementation design checked on **2026-11-11** and reviewed on **2026-11-12**, after the script adapter ([TASK-05C](task-05c-multi-instance-script-activities.md)) landed in commit `988cf0c`; its descriptions of the then-current code are historical.

This subtask is one part of TASK-05's first release. Completing REST support does not complete the parent: script and call activities remain required in that same release. TASK-05A is authoritative for the shared JSON schema, item lifecycle, persistence, admission, aggregation, read APIs, authorization, and rollout; this page specifies the REST behavior and integration work.

## First release acceptance criteria

- [ ] A REST `serviceTask` with `multiInstance.source: "collection"` and `asyncBefore: true` uses the core coordinator in both sequential and parallel modes. Body entry never makes an extra ordinary service invocation.
- [ ] Every admitted item owns one durable activity job with stable item/job identity, frozen inputs, existing HTTP timeout/response limits, and the core's item/lease fences. Empty input makes no HTTP request.
- [ ] Exact-name templates can read `mi.item`, zero-based `mi.index`, `mi.total`, frozen instance inputs, and declared shared aliases plus `config.*`/`setting.*` values and clock captured when the item first stages. JSON items can be sent whole; no dotted item-path lookup is introduced.
- [ ] `service.outputMappings` and `service.statusVariable` target declared item locals or declared `readWrite` shared aliases. The core atomically commits a successful item's shared write batch, local results, and completion under shared value-version fences; mapping/default/type/validation failures publish no partial batch.
- [ ] REST failures preserve existing exact/range/catch-all boundary matching and `boundaryFirst`/`retryFirst` behavior. An exhausted uncaught failure opens an incident while the body waits. A caught failure publishes the core's partial result before the boundary continues. The boundary's error variable receives a stable code, and a failed response's status reaches the workflow only through the boundary it selects (§4).
- [ ] Retry, restart, queue-capacity recovery, and shared-item/aggregate publication recovery do not invoke a successfully executed item again once its mapped result is durably saved as the item's candidate. At-least-once external effects and authored idempotency keys are documented accurately.
- [ ] Body cancellation, an interrupting timer/conditional boundary, or a caught failure cancels/fences unfinished work. A late HTTP response cannot commit locals or shared writes, advance the parent, or overwrite its aggregate. Non-interrupting timer/conditional boundaries leave the body running and create their own continuation under the core's subscription/firing rules.
- [ ] Editor authoring, import/export, validation, markers, runtime item detail, and authorized incident navigation work without changing ordinary REST tasks or human MI behavior.
- [ ] Until the parent's integration step, service bodies publish and run only on a Development host with the release switch open. Call bodies stay rejected until TASK-05D lands, and every other environment rejects all three kinds (§1).
- [ ] REST-specific automated tests, controlled HTTP integration scenarios, a recorded scale run, prepared examples and documentation, and real-browser evidence satisfy the completion handoff below. Behavior guides and canonical examples merge in the parent's integration change (§8).

## Scope, non-goals, and current evidence

Support only the existing REST connector on `serviceTask`. The body keeps one unconditional outgoing flow. `asyncBefore: true` is required; optional `asyncAfter` applies once after aggregate publication. Error, timer, and conditional boundaries attach to the body, never individual items. Timer/conditional boundaries support both interrupting and non-interrupting behavior through TASK-05A; conditional predicates keep the existing instance-only contract and shared writes do not create conditional wakes. The core rejects cardinality, early completion, manual item actions, and direct per-item writes to ordinary instance variables. Shared collection sources, staged shared reads, permitted shared targets, and shared aggregate/error targets are first-release work.

This subtask does not add connectors, an expression-based HTTP template language, compensation, selective retry-all/skip endpoints, per-item boundaries, or a physical exactly-once guarantee. It adds no receiver throttling either: a `429` or `Retry-After` response is an ordinary failed attempt under the node's retry delays, which every item shares. Authors limit pressure on a receiver with `maxConcurrency`, and operators with `Workflow.MultiInstance.MaxConcurrency`. It does not duplicate the coordinator, item tables, capacity reservation, aggregate completion job, or common runtime screens owned by TASK-05A.

Current implementation evidence:

- `WorkflowEngineService.Jobs.cs` already implements `StageAndInvokeServiceJobAsync`, `BuildStagedServiceInvocation`, `RestoreStagedServiceInvocation`, `ApplyStagedServiceResultAsync`, and `ShouldRetryBeforeBoundary`. These are the starting points for factoring REST mechanics.
- Ordinary durable service finalization writes process outputs and advances a token whose single `WaitingJobId` owns the job. An MI item instead settles through the core's item fence and local-output contract; widening ordinary job checks is insufficient.
- `ServiceTaskTemplating` performs exact dictionary lookup. URL/header substitution is strict; body substitution has permissive missing-value behavior. Resolve declared shared bindings during staging and fail an unavailable/invalid binding explicitly rather than silently substituting JSON null.
- TASK-05C (commit `988cf0c`) implemented the shared entry and item routing, the stage, save, and publish transactions, and the authored-failure and refusal exits in `WorkflowEngineService.AutomaticMultiInstanceScripts.cs`. `AutomaticMultiInstanceRules.InstalledAdapterKinds` holds `scriptTask` only. Its helpers reject any node that is not a script task and carry no HTTP status (§1).
- `AutomaticMultiInstanceRules.ItemOutputTargets` already lists a service body's mapping and status targets. `CaptureItemContextAsync` already persists an item's staged shared and environment values once and adds a stable `sys.jobId` and the current `sys.jobAttempt` for service and script items.
- Ordinary `WorkflowJobRepository` incident retry clears the saved result and, for `output_version_conflict`, the staging snapshot. The foundation already added the separate branch: `RetryIncidentAsync` treats an item whose candidate is saved as publication-only and keeps it. An item without a saved candidate is retried in full, so a success must be saved as a candidate before anything else can fail (§4).
- A leased `multiInstanceItem` job goes straight to the adapter dispatch (`WorkflowEngineService.Jobs.cs`); it never reaches the ordinary result-ready finalization that `SaveResultReadyAsync` feeds.
- `WorkflowVariableStore.MergeEffectiveValuesAsync` raises an operational conflict when an automatic phase statically reads a shared alias that has no current value, except for the collection source at entry, which is `multi_instance_source_invalid`.
- `HttpServiceTaskInvoker` already supplies the HTTP execution boundary. Preserve its established request, timeout, cancellation, response-size, and error behavior instead of introducing an MI HTTP stack.
- `WorkflowModelMigrator`, editor normalization, and validation currently restrict MI to human tasks. The core owns the common change; this adapter supplies REST-specific validation and import/authoring cases.

## Plan

### 1. Integrate through the shared core

Agree the adapter boundary with TASK-05A before changing ordinary REST execution. The core supplies the immutable body context, copied item/index/total, captured causal actor/allowlisted claims, local declarations/defaults, shared access plan, stable job identity, and eligible item/parent state. The REST adapter stages an invocation and returns a bounded candidate containing local results and shared writes, or the existing classified failure outcome. The coordinator owns all persistence and publication fences; the adapter never routes the token or writes runtime variables itself.

| Responsibility | Owner and integration rule |
| --- | --- |
| Entry and item jobs | TASK-05A's `InitializeBodyAsync` transfers the parent `asyncBefore` job into the body wait and its dispatch admits `multiInstanceItem` activity jobs. Calling it from the entry job and executing an item job are adapter work (see **Release boundary** below). TASK-05C already routes both jobs to one dispatch by node type (`ProcessAutomaticMultiInstanceEntryJobAsync`, `ProcessAutomaticMultiInstanceItemJobAsync`); this adapter adds the service case. REST entry must not call `ExecuteServiceTaskAsync` once before fan-out. |
| Staging/invocation | This adapter reuses existing REST template construction and invoker mechanics against frozen body inputs and the item's saved shared/settings/clock context. |
| Mapping/failure outcome | This adapter validates local/shared output and status candidates and returns success or the existing service failure classification, including eligible HTTP status. |
| Item persistence and refill | TASK-05A checks shared `ValueRevision` fences and atomically commits the validated shared batch, locals, item/counts/ledger, and durable dispatch/completion signal. Capacity contention cannot undo accepted success and trigger another HTTP invocation. |
| Aggregate and continuation | TASK-05A alone resets/publishes `resultVariable`, fences concurrent writes, processes variable notifications, and performs the single outgoing/async-after continuation. |
| Limits, version changes, retention | Use the core's captured concurrency, item/snapshot/result bounds, active-work retention, and open-body version blocker. Do not add REST-only competing settings or lifecycle state. |

The common schema/migration and core runtime must be present before this adapter runs. Do not ship a REST-only feature gate that allows incomplete cancellation/recovery or suggests the parent release is finished without calls.

**Reuse the script adapter's item protocol.** `WorkflowEngineService.AutomaticMultiInstanceScripts.cs` already implements entry, the item-owner lock, staging, candidate save, publication, the authored-failure exit, and the refusal exit. Those helpers are script-specific today: `LockAutomaticScriptItemOwnerAsync` and the entry path reject any node that is not a script task, and the failure handlers build a `TaskExecutionOutcome` with no HTTP status, so exact and range boundary matching cannot work through them. Before adding REST behavior, make them kind-neutral in a change that leaves script behavior and script tests unchanged: the owner lock and entry accept any installed kind, and the failure exit takes the caller's outcome, status included. Do not copy them into a second file. That change also settles TASK-05C's open lock finding, because this adapter reuses the helper: it locks the job before the item row, while the documented order puts items first.

**Release boundary.** The publication, runtime, and item-job guards read `AutomaticMultiInstanceRules.InstalledAdapterKinds`, which holds `scriptTask` only, so no service body can be published or run today, even on a Development host. Open them for service bodies only, as [TASK-05A §8](task-05a-multi-instance-core.md#8-definition-validation-compatibility-and-safety-of-loading) records under **Opening by activity kind**:

- A service body publishes only when the release switch is enabled, which only a Development host allows, and every automatic body in its definition has an installed adapter. This is not the REST-only gate ruled out above: no other environment can open it, and the integration change removes it.
- The service adapter counts as installed only in the change that completes its entry, item, failure, and cancellation paths.
- [TASK-05C](task-05c-multi-instance-script-activities.md) landed first and added the dispatch by node type that `WorkflowEngineService.Jobs.cs` calls for a body's `asyncBefore` entry job and for every `multiInstanceItem` job. This adapter adds the service case to both routes and `serviceTask` to `InstalledAdapterKinds`.
- Tests pin that call bodies stay refused at publication, entry, and item processing until TASK-05D lands, and that a host outside Development refuses service bodies whatever its options say. The existing tests that pin service bodies as refused change to call bodies only: the publication case in `AutomaticMultiInstanceScriptRuntimeTests`, the entry and item cases in `AutomaticMultiInstanceProcessorTests`, and any foundation test with the same assertion.

### 2. Proposed REST workflow JSON

Use the core's `multiInstance` shape and dedicated local declarations. This illustrative node requires an instance-scoped `supplierIds` string array and a different writable scalar-JSON `notificationResults` process variable initialized to `[]`:

```json
{
  "id": 20,
  "name": "Notify suppliers",
  "type": "serviceTask",
  "asyncBefore": true,
  "multiInstance": {
    "mode": "parallel",
    "source": "collection",
    "collectionVariable": "supplierIds",
    "completionEvaluation": "afterAll",
    "maxConcurrency": 4,
    "outputVariables": [
      { "name": "notificationId", "dataType": "string", "isArray": false, "nullable": false },
      { "name": "responseStatus", "dataType": "number", "isArray": false, "nullable": false }
    ],
    "resultVariable": "notificationResults"
  },
  "service": {
    "type": "rest",
    "method": "POST",
    "url": "https://notifications.example.com/send",
    "headers": [
      { "name": "Content-Type", "value": "application/json" },
      { "name": "Idempotency-Key", "value": "flowbit-supplier-${sys.jobId}" }
    ],
    "body": "{\"supplier\":${mi.item},\"index\":${mi.index}}",
    "statusVariable": "responseStatus",
    "outputMappings": [
      { "variable": "notificationId", "path": "id", "dataType": "string", "isArray": false, "required": true }
    ]
  }
}
```

This is proposed JSON, not a currently runnable definition. The destination is illustrative. Canonical executable examples must use a documented controlled endpoint that returns an `id` and records invocation/idempotency evidence. For sequential execution, use `mode: "sequential"` and omit `maxConcurrency` or set it to 1. The core owns allowed ranges, defaults, normalization, and output-declaration limits.

`${mi.item.customerId}` is not a supported new path syntax. Prefer sending the whole item in a JSON body using `${mi.item}`. Preserve existing URL/header text conversion: object/array values become raw JSON text, subject to ordinary URL/header validation; this feature does not introduce a new rejection rule for them. Do not add a different expression parser to the REST adapter.

### 3. Frozen template staging and local output contracts

Instance inputs and the item come from the body snapshot taken at successful body initialization, after the parent async-before wait. A shared collection is copied once at that instant and later catalog changes do not change its items; its alias and any source resolving to that same shared key use the frozen collection inside templates. Other shared inputs, template `config.*`/`setting.*` values, and `sys.now`/`sys.today` are read when the item first stages and kept in its staged snapshot for retries. A later item may therefore see a newer shared value or rotated setting; sequential items can observe earlier items' committed shared writes. Earlier locals and subsequent instance edits remain invisible. Preserve existing job/attempt metadata behavior; `sys.jobId` stays stable across retries of that item.

**What is frozen.** A REST body's references are static, so the adapter names them exactly, as the script adapter does for NCalc: the placeholders of the URL, header values, body, and string defaults of mappings and local outputs (`ServiceTaskTemplating.GetPlaceholderNames`), and the parameters of mapping and local-output validations (`SequenceFlowConditionEvaluator.ParameterNames`). The body freezes the instance variables among those names, never its collection or its aggregate target. Each item captures only the `config.*`/`setting.*`/`sys.*` names among them. Do not reuse the ordinary job's `FilterSnapshotContext`: it matches names by text, and an over-included large variable could fail entry at the 1 MiB snapshot limit. Render the request from the item's saved context on every attempt, as `RestoreStagedServiceInvocation` does for an ordinary job; do not store the rendered URL, headers, or body.

**Frozen settings and credentials.** A captured `config.*`/`setting.*` value stays in the item's staged input until the body settles, and every retry of that item reuses it, as an ordinary durable service job reuses its snapshot. An item that fails because a credential expired therefore cannot be recovered by rotating the setting and retrying; only items that stage later read the new value. Without item skipping, that item waits until an error boundary, an interrupting boundary, or cancellation ends the body. Document this with the stuck-body limit in §4, and keep staged values out of item DTOs, logs, and metrics.

Apply these rules consistently in the server, editor save validator, live field validation, and runtime guard:

1. Reuse strict URL/header substitution, JSON-body substitution and validation, HTTP method/header rules, and existing staged-invocation restoration. Keep exact-name `mi.item`, `mi.index`, and `mi.total` available in the item context. Progress counters such as `mi.completed`/`mi.remaining` are not inputs to item requests.
2. Include declared readable shared aliases used by URL/header/body placeholders, defaults, and validations in the core's staged shared-access plan. Preserve catalog binding/type/access checks, canonical key ordering, and current missing/null semantics. An invalid, missing, or archived binding fails explicitly before invocation; ordinary legitimate nullable values keep their contract. A readable binding whose value was never set also stops staging: the core raises an operational conflict for a statically read alias with no current value, so the item waits in an incident until the value is set and takes no error boundary. This differs from an ordinary REST task, where a missing URL or header variable is a task failure a boundary can catch. TASK-05C lists this case as untested; pin the incident type here. Aggregate and boundary validation use the core's publication context and access plan.
3. Resolve `outputMappings[].variable` against local declarations or declared `readWrite` shared aliases. Reuse path extraction, required/default behavior, type/array checks, validation order, and the ordinary all-or-nothing mapping batch. A mapping may not target an ordinary instance variable, read-only shared alias, undeclared name, reserved context name, or aggregate target. Enforce the core's alias/canonical-key collision rules, including its separation of collection, aggregate, item-write, and error targets.
4. Resolve `statusVariable` against a declared scalar-number local or `readWrite` shared alias. It cannot overlap another output mapping by alias or canonical shared key. Local status is not a process producer; a shared status target is an explicit shared producer in the access and lock-order plans.
5. Apply defaults to a fresh local buffer for an execution/mapping attempt and validate the final overlay before accepting success. Publication-only retry retains the saved candidate instead. Every declared output must satisfy the core's completion contract; nullable unset locals become JSON null, and an empty declaration list yields `{}` for a side-effect-only request.
6. A failed execution or mapping attempt discards both locals and staged shared writes; a publication conflict retains its successful candidate. A service failure may retain its status in bounded item diagnostics, but publishes neither a local success nor a shared status/mapping batch. The selected whole-body error boundary may write its declared instance or `readWrite` shared error target through the core's fenced boundary publication. Unlike an ordinary REST task, which writes `statusVariable` on a failure too, a failed item delivers no status to the workflow: failed rows of the partial result carry empty `variables`, and the error variable receives a stable code (§4). The status-matched boundary that was selected is the only status information the handler path has. The failed item and its node visit record a description that names the status, or says no response arrived, without request or response data, and item detail shows it to callers with node-activity read permission.

Factor pure request construction and result-to-candidate mapping from persistence. Keep ordinary `ApplyStagedServiceResultAsync` semantics intact for non-MI work, including ordinary shared status/output handling. Exclude MI locals from process-producer inference while retaining declared shared targets and their validation dependencies in access/readiness/lock-order analysis. The aggregate contains local results; shared writes retain their normal catalog/audit evidence and bounded value-free item correlations, not invented local declarations or copied values in ordinary instance history. Include shared input and candidate data in the core's combined 1 MiB staged-payload limit and retain candidates independently of completed job cleanup.

### 4. Durable execution, retries, and cancellation

The core owns the transaction/lock order. REST follows its staged protocol:

| Phase | REST-specific behavior |
| --- | --- |
| Stage | Verify body/item/job/lease ownership and call `CaptureItemContextAsync` with the referenced `config.*`/`setting.*`/`sys.*` values. It reads shared aliases and captures target `ValueRevision` stamps under canonical shared-key locks after runtime locks, and persists the bounded item context once. Commit, discard tracked runtime state, then render the request from that context (§3). |
| Invoke | Call the existing HTTP invoker without holding instance, item, or shared row locks. Honor cancellation and existing timeout/response limits. |
| Map and save | Map the response in memory with no lock held: path extraction, defaults, type checks, the status target, and mapping, local, and shared-target validation over the item's saved context. Commit the resulting candidate with `SaveCandidateAsync` in its own transaction. The item's candidate is the only durable record of a success. Do not use the job's result-ready row (`SaveResultReadyAsync`): item jobs never reach its finalization, and incident retry does not consult it. A failed response or a failed mapping is not saved. |
| Publish | Call `PublishCandidateAsync` in a further transaction. It reacquires fresh runtime and canonical shared-key locks, verifies item/activation/lease and output-value fences, and commits the whole batch and item success once. An item job that finds a saved candidate goes straight here on every retry. Do not clear the parent's wait or route it per item. |
| Shared publication conflict | Retain response, candidate values, frozen inputs, and item/job identity. Open the core publication incident and retry only fenced publication after authorized fence refresh; never repeat HTTP invocation to resolve this conflict. |
| Failure | Return the existing classified service outcome to the common retry/boundary/incident flow; retain item/job identity and the frozen request inputs. |

Map every failure to one coordinator exit, as [TASK-05C §5](task-05c-multi-instance-script-activities.md#5-sandbox-jobs-failures-and-cancellation) does for scripts:

| Failure | Detected | Exit |
| --- | --- | --- |
| An invalid, unset, or oversized collection at entry (`multi_instance_source_invalid` or a size code). | Coordinator, during initialization. | The entry job's `job` policy. The failure has no HTTP status, so only a catch-all boundary catches it; a host with only status-matched boundaries retries and then waits in an incident. |
| A template that cannot render (a strict URL or header name with no value, a rendered body that is not JSON), a non-2xx response, a timeout, a transport error, or a response over `MaxResponseBodyBytes`. | Adapter, while rendering or invoking. | Authored failure with code `service_task_failed`: the node's `job` policy, with the boundary selected by the response's status (exact, then range, then catch-all). Without a selectable status only a catch-all matches. |
| A 2xx response whose mapping fails (path, required, type, default, or mapping validation), an invalid status target, a non-nullable local left unset, a false local validation, or a shared write failing its catalog contract. | Adapter, while mapping. `SaveCandidateAsync` repeats the check. | Authored failure with code `multi_instance_result_invalid`, selected as an ordinary mapping failure is. Nothing was saved, so a retry sends the request again. |
| A local validation that passes while mapping and fails against the shared values current at publication. | `PublishCandidateAsync`. | The saved candidate's authored failure, as TASK-05C decided: it follows the `job` policy and never sends the request again. The candidate holds no response status, so only a catch-all boundary catches it. |
| Staged input over 1 MiB, staged input plus candidate over 1 MiB, or accepted locals taking the body past its result limit. | Coordinator. | `RefuseItemResultAsync` in a fresh transaction: never retried and never left in an incident. The body takes its catch-all boundary or faults with `multiInstanceFailed`; status-matched boundaries do not apply. |
| A shared output `ValueRevision` changed since staging. | `PublishCandidateAsync`. | `output_version_conflict` incident over the saved candidate; authorized retry refreshes the fence without another request. |
| A missing or archived binding, access or contract drift, a statically read shared alias with no value, a lost lease, or a stale activation. | Coordinator. | Operational conflict or incident; never an error boundary. |

The boundary's error variable receives the code, as a script body's does, not the reason text an ordinary REST task writes; the reason stays in the item's bounded diagnostics. Uncaught failures keep the existing retry schedule and the `job_execution_failed` incident. The codes, the missing status on the handler path, and the catch-all-only cases were agreed on 2026-11-12 (see [Plan review](#plan-review-2026-11-12)).

Preserve `boundaryFirst` as the existing default. With a matching error boundary, it catches the first applicable failure; `retryFirst` retries before taking that boundary after exhaustion. With no matching boundary, apply the normal retry schedule and then open the item's incident. Preserve exact HTTP-status, status-range, and catch-all selection, including non-HTTP failures such as timeout and an authored candidate violating a valid target value contract. Missing/archived bindings, binding access or contract drift, and `ValueRevision` conflicts are operational incidents, not business failures caught by an error boundary. Once work has produced a saved candidate, recovery of those publication failures retains it and never invokes HTTP again; before invocation, the staging incident can retry only after its binding problem is resolved. Do not treat a transient response as successful merely because its body can be mapped.

A caught item failure interrupts the entire body once. The core keeps completed siblings, faults the winning visit, cancels/fences unfinished jobs and pending items, publishes its partial result, and routes the boundary. An uncaught incident occupies its slot and pauses new admission according to the core; already admitted siblings can settle. An execution retry resumes the same failed item job, never successful siblings; publication recovery retains its saved candidate. The recoverable node visit stays active; terminal ledger state is not reopened. A response that repeats on every attempt, such as a 4xx the receiver always returns for one item, keeps an uncaught body waiting unless the instance/body is cancelled or an interrupting timer/conditional boundary fires. An item that staged a credential which has since expired is the same case, because its retries reuse the staged value (§3). This is the core's documented first-release limit; recommend an applicable error boundary or interrupting timeout/condition for batches that must finish.

Instance/scope cancellation, termination, parent-call cancellation, or a winning error or interrupting timer/conditional boundary invalidates item publication immediately. A cancelled request may still finish externally. Its late result must fail the refreshed item/body fence without local/shared writes, aggregate writes, audit continuation, or new admission. Already committed shared writes and remote effects are not compensated. Non-interrupting boundary continuations do not settle the body or release item slots; the core owns their firing limits, activation lifetime, cancellation races, and routing.

Use the core's body activation timeline: conditional subscriptions start at token entry, including while entry waits for another body at that node; timers start once at successful body initialization before aggregate reset/admission. Both remain active through incidents and completion and close before `asyncAfter`. A caught error first closes those subscriptions, then follows partial/error publication. An interrupting timer/condition cancels unfinished work and routes once without publishing a new partial aggregate; already accepted writes, including an aggregate that triggered the condition, remain and suppress normal continuation. Non-interrupting firings create sibling tokens, with already latched occurrences/spawned siblings surviving body completion under occurrence fences. Add no per-item subscriptions.

REST transport is **at least once**. A request can succeed remotely before its response is durably recorded, or an old request can outlive a lost lease. A replacement attempt may therefore reach the receiver again. Recommend authored receiver-supported idempotency headers based on stable `sys.jobId`, as in the example; do not vary that key by retry attempt. A new body visit creates new item/job identities. The core's strict logical admission bound does not promise a physical maximum of remote requests still running after lease loss.

Shared item output conflicts and instance/shared aggregate conflicts are publication incidents over saved candidates/results. Authorized retry refreshes only the affected output fences and retries publication without HTTP invocation. It does not recalculate or merge a stale read-modify-write value; accepting that retry can overwrite a newer shared value with the saved candidate, and Operations must explain this. Parallel items writing one shared key can conflict; neither completion order nor index order grants automatic overwrite authority. Keep the candidate independent of generic job result cleanup. The foundation's `RetryIncidentAsync` already treats an item with a saved candidate as publication-only: it keeps the candidate and requests a fence refresh only for `output_version_conflict`. Pin it with REST cases and receiver request counts instead of adding another branch. Deterministic aggregate failures take the boundary or fault the instance as the core specifies. A full queue uses reserved dispatch/completion, never remote reinvocation. Shared output commits, aggregate/error publication, boundary routing, and synchronous continuation must all pass the core's monotonic shared lock-order proof.

### 5. Validation, editor, and operational UI

Add REST-specific rules to the shared automatic-MI validation owner: required async-before, one ordinary outgoing flow, local-or-writable-shared output/status targets, shared input/binding/validation dependencies, canonical-key collisions, and permitted body error/timer/conditional boundaries. Preserve instance-only conditional predicates and the existing timer-expression rules. Existing malformed imported settings must remain visible until explicitly edited/removed. Do not normalize unsupported MI input into an ordinary synchronous task.

In the canonical editor, expose the core MI section on service tasks. Enabling it enables async-before and reviews existing output mappings/status targets; permit declared locals and writable shared aliases without silently redirecting ordinary instance writes. Disabling MI or converting the node type must surface/confirm incompatible settings and must not silently promote locals to process producers. Offer body timer/conditional attachments and their interrupting setting through the core controls. Integrate accepted changes with existing undo/history handling and `changeNodeTypeFromInspector`.

Show local declarations and clearly distinguish local, shared, and unavailable ordinary-instance targets. Help must explain collection/instance snapshots, shared/settings/clock capture per item, shared publication conflicts and retry acceptance, exact-name templates, optional once-per-body async-after, Worker dependency, error and interrupting/non-interrupting boundary behavior, partial results on a caught error, and idempotency. Render sequential/parallel MI markers on the service shape; preserve accessible labels, field help, imported diagnostics, hosted dirty/saved state, and AI snapshot/apply behavior. No new editor dependency is required.

Use TASK-05A's paged automatic-item views, execution kind, read routes, and Operations authorization. Add REST status/failure and stable job correlation where appropriate; no new complete-item or retry-all API is introduced. Ordinary instance visibility does not confer job-retry authority. Do not expose staged authorization headers, full HTTP requests, credentials, or snapshot internals in item DTOs, logs, metrics, or UI. Bounded diagnostics must follow the existing redaction contract.

### 6. File ownership and implementation sequence

Paths below are relative to the repository root. New adapter filenames, if needed, are implementation proposals rather than existing APIs.

| Area | Principal files and boundary |
| --- | --- |
| REST job adapter | `Flowbit/src/Flowbit.Service/Services/WorkflowEngineService.Jobs.cs`, `WorkflowEngineService.AutomaticMultiInstanceScripts.cs` (the shared item protocol and dispatch, made kind-neutral in §1), `AutomaticMultiInstanceRules.InstalledAdapterKinds`, relevant service methods in `WorkflowEngineService.cs`, and the automatic-MI coordinator seam supplied by TASK-05A. Factor REST mechanics without copying the coordinator or the script adapter. |
| HTTP/templates | `Flowbit/src/Flowbit.Service/Services/ServiceTaskTemplating.cs`, `Flowbit/src/Flowbit.Infrastructure/Http/HttpServiceTaskInvoker.cs`; preserve ordinary behavior and change only where shared factoring or precise guards require it. |
| Rules/models | `WorkflowDefinitionValidator.cs`, the core automatic-MI rule helper, `Flowbit.Shared/Models/WorkflowModel.cs`, `WorkflowModelMigrator.cs`, `CallActivityRules.SourceDeclarations`, shared-access/readiness analysis, and existing typed-output/error-boundary helpers. Coordinate shared-file edits with TASK-05A. |
| Editor/UI | `Flowbit/src/Flowbit.Ui/wwwroot/editor/flowbit-editor.js`; template/CSS only as required. Integrate REST presentation with core instance item views and Operations correlation. |
| Tests/examples | `Flowbit/tests/Flowbit.Tests/`, the separate browser suites, and prepared example definitions kept as test fixtures outside `examples/` until the parent's integration change (§8). |

1. **Characterize ordinary REST behavior.** Pin template substitution, mapping/status validation, boundary selection, and staged response recovery. The shared-contract decisions in the [plan review](#plan-review-2026-11-12) are agreed; raise any new one with TASK-05A before refactoring.
2. **Make the script adapter's item protocol kind-neutral (§1).** Change no behavior: the owner lock and entry accept an installed kind, the failure exit takes an outcome with its status, and the item-owner lock order is settled. Gate on every script suite passing unchanged, with service bodies still refused.
3. **Implement REST validation and staging.** Accept the shared MI schema only through the core path; add local/shared mapping/status validation, the exact frozen names of §3, and request rendering from the item's saved context. Gate on API/editor parity and ordinary REST regression tests.
4. **Integrate durable invocation, candidate save, and publication.** Attach the existing invoker to item ownership, map in memory, and save the candidate (§4); then add `serviceTask` to `InstalledAdapterKinds` and flip the tests of §1. Gate on both modes, out-of-order results, no extra entry request, and empty-input behavior.
5. **Integrate failure and recovery.** Exercise every row of the §4 failure table, retry precedence, status matching, incident recovery, lost leases, late responses, queue pressure, and aggregate conflict recovery. Prove successful request counts do not increase during unrelated recovery.
6. **Complete authoring and operations UX.** Add controls/help/markers and REST-specific item diagnostics through the core screens. Exercise both standalone and hosted editors and Worker-enabled runtime behavior in real browsers.
7. **Record the scale run, prepare documentation/examples, and hand over evidence.** Follow the timing in §8, run the shared release gates with the other adapters, and hand results to TASK-05. Do not add a separate schema or rollout path for REST alone.

### 7. Automated and real-browser verification

Add focused REST MI cases under `Flowbit/tests/Flowbit.Tests`; proposed scenario groups are not claims that new tests already exist.

| Group | Required evidence |
| --- | --- |
| Definition/editor parity | Required async-before; both modes; local/shared output/status types/access and alias/key collisions; shared collection/URL/header/body/default/validation dependencies accepted with valid readable bindings; invalid/archived bindings and read-only output targets rejected; body timer/conditional attachment and interrupting flags; wrong/missing/null settings; imported invalid values survive redraw and round-trip. Ordinary REST/human MI behavior stays unchanged. |
| Templates and mappings | Scalar and JSON whole-item bodies, exact item/index/total names, scalar URL/header rules, escaping and JSON validation, no implicit item paths, shared collection copied once, frozen instance inputs, shared/settings/clock read at item staging and retained across retries, sequential observation of earlier shared commits, mapping defaults/required/null/array/type rules, final local completeness, empty locals, and atomic local/shared/status results. |
| Durable success and admission | Parallel responses complete in reverse order but aggregate by index; sequential sends no next request before prior item success commits; empty array sends none; duplicate inputs remain separate items; multiple Workers and queue pressure respect core admission. |
| Failure/recovery | Exact/range/catch-all/no-match; timeout/transport/invalid response/mapping/status failures; boundary-first versus retry-first; partial result published on a caught failure; exhausted incidents and same-item retry; several incidents; successful siblings stay untouched. |
| Crashes and side effects | Gates around stage, remote effect, candidate save, publication, and aggregate commit; restart and lease loss preserve stable idempotency headers and single local completion. A crash before the candidate is saved sends the request again with the same `sys.jobId`; prove that publication of a saved candidate and committed-success recovery make no extra HTTP request. |
| Cancellation and conflicts | Parallel items and other instances updating the same shared key; value changes conflict while description-only changes do not; shared item/aggregate conflict retry preserves saved candidates and HTTP counts; invalid shared batch publishes nothing; body/instance/scoped/parent-call cancellation and interrupting error/timer/conditional boundaries discard late responses; committed shared writes remain. Exercise conditions while entry waits for another body, timer activation before reset, incident/completion lifetime and closure before async-after; error capture closes subscriptions before partial/error writes; interrupting timer/condition publishes no new partial aggregate and preserves triggering accepted writes; non-interrupting repeats/latches/spawned siblings survive as specified by core occurrence fences; no shared-write conditional wake. |
| Limits/security | Core item/snapshot/result limits include shared contexts/candidates plus HTTP response bounds; no truncation; shared-binding guard before substitution; canonical multi-key lock order including boundary/continuation paths; no secrets in DTOs/logs; denied job inspection/retry; automatic items absent from human inbox/actions. |
| Release boundary | With the script and service adapters installed: service bodies publish and run on a Development host with the switch open; call bodies are refused at publication, entry, and item processing; a host outside Development refuses service bodies whatever its options say. Script suites pass unchanged after the kind-neutral change. |
| Failure exits | One case per row of the §4 failure table, with and without a boundary and under both `failureHandling` values. Entry failures, refusals, and a validation failing only at publication reach a catch-all boundary and never a status-matched one; a host with only status-matched boundaries waits in an incident or faults as the table says. The error variable holds the stable code. A retry after a committed candidate sends no request; a retry after a failed mapping sends one. |
| Adapter contract | Exact frozen instance names and per-item `config.*`/`setting.*`/`sys.*` names, including a large stored variable no template names; the request rendered again from saved context on retry with the same `sys.jobId`; a setting changed between an item's attempts keeps its staged value while a later item reads the new one; a statically read shared alias with no value waits in an incident; no rendered request or staged secret in item DTOs or logs. |
| Scale | A 1,000-item body against the controlled receiver in both modes, with default Worker settings and with several Workers, recording duration, queries per item, and connection use. It measures the per-item cost TASK-05A lists as an open finding (each completion reads progress for every body of the instance and lists its open jobs) under real invocation waits. |

Extend relevant existing suites: `ServiceTaskTemplatingTests`, `HttpServiceTaskInvokerTests`, `ServiceTaskSharedStatusTests`, `ErrorBoundaryMatchingRuntimeTests`, `DurableWorkflowProcessorTests`, `DurableJobRepositoryTests`, `DurableJobConflictTests`, `DurableParallelFinalizationTests`, `EditorValidatorTests`, and `EditorValidatorCharacterizationTests`. Coordinate the broader MI, projection, shared-variable, automatic-loop, retention, and version-switch regression gates with TASK-05A.

Run focused tests while implementing, then the shared final gate from the repository root. These commands work in PowerShell 7 and Bash:

```text
dotnet test Flowbit/tests/Flowbit.Tests/Flowbit.Tests.csproj --nologo --verbosity quiet
dotnet test Flowbit/Flowbit.slnx --nologo --verbosity quiet
git diff --check
```

Use a controlled local HTTP receiver with gates, call counts, and idempotency evidence; no live external provider is required. PostgreSQL-backed tests require Docker. An unavailable dependency is not a passing test, and broad suites need not be repeated without a change or unresolved failure.

Follow the [Chromium smoke setup](../../Flowbit/tests/Flowbit.BrowserTests/README.md) for editor checks and the [Worker acceptance setup](../../Flowbit/tests/Flowbit.BrowserAcceptanceTests/README.md) for durable runtime checks. Keep the ordinary smoke fixture Worker-free. Acceptance uses the real localhost API/UI/Worker, disposable database, controlled receiver, and authenticated HTTP setup. Gates may delay real responses; they may not rewrite them or manufacture application state through database edits.

At **1440×900, 1024×768, and 390×844**, use real clicks, typing, keyboard navigation, and relevant pointer interactions to:

1. Create/import parallel and sequential service bodies; configure instance/shared collections, local/shared status/mappings, body timer/conditional boundaries and interrupting flags; save/reopen, inspect malformed inputs, change type, and undo/redo in standalone and hosted editors.
2. Start with the Worker stopped, inspect queued state, start it, and inspect ordered results while the receiver releases responses in reverse order. Exercise empty input.
3. Cause a real transient failure and exhausted incident; inspect the authorized Operations link, retry through the UI/keyboard, and verify successful sibling call counts do not increase.
4. Hold a response, cancel the body/instance or fire an interrupting timer/conditional boundary, release it, and verify no late local/shared commit or normal continuation. Exercise non-interrupting timer/conditional continuations while the body keeps running. Inspect a caught error's partial result, then shared item and aggregate publication conflicts; authorize publication retry and verify the receiver sees no new request.
5. Check paged item results, loading/error states, identity-change stale responses, denied detail/retry, and the open-body version-switch blocker without changing human MI screens.

Inspect console errors/warnings and host logs. Capture the service MI controls/marker and queued, running, blocked, completed, and cancelled runtime screens. Record browser/version, exact localhost URLs, interactions, viewport sizes, console results, screenshots, and unavailable checks. Unit/Jint/DOM-stub tests do not replace browser verification.

### 8. Documentation and completion handoff

This planning change updates only the proposed contract. Follow the timing in [TASK-05A §13](task-05a-multi-instance-core.md#13-documentation-and-completion-handoff), as TASK-05C does:

- **With this subtask's code:** implementation invariants in [AGENTS.md](../../AGENTS.md) and the [runtime reference](../../Flowbit/README.md), and any operational, setting, schema, or route reference this subtask changes, marked inactive until automatic multi-instance is enabled.
- **Prepared here, merged in the parent's integration change:** the REST sections of the behavior guides in the table below and the canonical examples. The AI authoring package bundles those guides and the example catalog, so merging them earlier would tell the assistant to author definitions that publication still refuses. Until then keep the example definitions as test fixtures outside `examples/`, run them over HTTP on the Development test hosts, and record the contract in this plan.

| Owning document | REST-specific implementation update |
| --- | --- |
| [BPMN support](../bpmn-support.md), [node/property reference](../node-reference.md) | Supported REST MI subset, required async-before/optional body async-after, exact templates, local/shared output/status rules, body error and interrupting/non-interrupting timer/conditional boundaries, and deferred features. |
| [Developer guide](../developer-guide.md) | HTTP-only walkthroughs, collection/instance snapshots and per-item shared/settings context, ordered instance/shared aggregate, receiver-supported idempotency, retry/boundary differences, partial results, the stable error code and the missing status on the handler path, the catch-all-only failures, the stuck-body limit, cancellation, and publication-only conflict recovery with saved-value overwrite semantics. |
| [API reference](../api-guide.md), [Flowbit.Ui guide](../ui-guide.md) | REST item/status/job correlation through the core contract; authoring controls, permissions, paged results, incident recovery, and changed screenshots. |
| [Deployment/operations](../deployment.md), [runtime reference](../../Flowbit/README.md), [AGENTS.md](../../AGENTS.md) | Shared staging/ValueRevision fences, unlocked invocation, atomic local/shared settlement, monotonic lock-order proof, publication-only recovery, retained candidates, boundary lifecycle, limits, Worker/capacity interaction, and redaction. For operators: settings and credentials staged per item and reused by its retries, so rotating a credential does not recover an item that already staged; no receiver throttling beyond `maxConcurrency` and `Workflow.MultiInstance.MaxConcurrency`; and the recorded scale figures. Shared migrations/settings/rollout remain owned by TASK-05A. |
| [Example catalog](../../examples/README.md), [MI examples](../../examples/multi-instance/README.md) | Controlled REST examples in both modes, object-item body, local/shared reads/writes and aggregate, side-effect-only outputs, empty collection, error/timer/conditional boundaries in both interrupting modes where applicable, and conflict recovery. State inputs, expected outputs/call counts, Worker/endpoint prerequisites, and idempotency support. |

Review [database schema](../database-schema.md) only if adapter integration changes core persistence, and return those changes to TASK-05A rather than documenting an independent model. Update root capabilities, setup instructions, or documentation links only when affected. Validate relative links/anchors/images and parse JSON fragments; exercise changed example walkthroughs over HTTP with equivalent PowerShell/Bash commands. Do not change AI tool names, descriptions, or instructions.

Before marking TASK-05B Done, attach actual automated and browser evidence here, confirm ordinary REST and human MI compatibility, and return the final adapter contract, example inputs/results, error/retry matrix, and any shared-core changes to TASK-05. Follow the parent's coordinated migration/publication/rollback plan. REST implementation is complete only when its required core integration and recovery checks pass; the parent remains open until script and call subtasks and the joint first-release gates are also complete.

## Plan review (2026-11-12)

This plan was reviewed against the parent plan, TASK-05A, TASK-05C's implementation record, and the code at commit `988cf0c`. No service-adapter code exists yet. The review changed the plan as follows:

- **Current state** records what TASK-05C and the foundation already provide: the entry and item dispatch, the item protocol, the list of installed kinds, and publication-only incident retry. The earlier text planned the dispatch and the retry branch again.
- **Reuse of the script adapter** (§1, §6 step 2) is new. Its helpers reject non-script nodes and carry no HTTP status, so they are made kind-neutral first instead of copied.
- **Release boundary** (§1) now names the installed-kind list and the existing tests that pin service bodies as refused.
- **Frozen names and credentials** (§3) say which instance variables a body freezes and which settings an item captures, and record that a rotated credential does not recover an item that already staged.
- **Map and save** (§4) replaces "persist response" and "finalize". The item's candidate is the only durable record of a success; the job's result-ready row is not used.
- **Failure exits** (§4) map every failure to one coordinator operation, including the cases only a catch-all boundary catches.
- **Tests** (§7) add the release boundary, failure exits, adapter contract, and scale rows.
- **Documentation timing** (§8 and the acceptance criteria) now follows TASK-05A §13. The earlier text updated behavior guides and canonical examples before integration, when publication still refuses them.
- **Receiver throttling** is recorded as out of scope.

Six decisions change or extend a shared contract. All six were agreed on 2026-11-12 as proposed, and TASK-05A records them under **Service adapter decisions** in its [§8](task-05a-multi-instance-core.md#8-definition-validation-compatibility-and-safety-of-loading). They are decisions for this subtask's implementation, not implemented behavior:

1. A successful response is mapped in memory and saved only as the item's candidate; the job's result-ready row is not used (§4).
2. A caught REST failure writes the code `service_task_failed` or `multi_instance_result_invalid` to the boundary's error variable, and no HTTP status reaches the handler path except through the boundary selected (§3, §4).
3. Entry failures, refusals, and a validation failing only at publication reach a catch-all boundary only (§4).
4. A statically read shared alias with no value is an operational incident, as the core behaves today, not a failure a boundary catches (§3).
5. TASK-05C's rule for a validation that fails only at publication applies to REST items unchanged (§4).
6. An item's staged settings stay frozen for its retries even when the failure is an expired credential (§3).

## Implementation record (2026-11-12)

The REST adapter is implemented on the TASK-05A foundation and the protocol TASK-05C
built. The status stays **In progress**: the Worker-enabled browser acceptance
scenarios, the scale run, and the prepared guide sections and examples listed under
[Still open](#still-open) are not complete.

### What changed

| Area | Change |
| --- | --- |
| Shared item protocol | The entry, owner lock, staging, candidate save, publication, authored-failure, and refusal code moved from the script adapter into `WorkflowEngineService.AutomaticMultiInstanceItems.cs`, unchanged for scripts except two points: the owner lock now takes instance, token, body, item, then job (TASK-05C's open lock finding), and the authored-failure exit takes a `TaskExecutionOutcome`, so a REST outcome carries its HTTP status. Entry and item jobs reach one dispatch by node type there. |
| Release boundary | `AutomaticMultiInstanceRules.InstalledAdapterKinds` holds `serviceTask` and `scriptTask`. Call bodies stay refused at publication, entry, and item processing. |
| Entry | `AutomaticServiceInstanceInputNames` freezes exactly the instance names that the URL, header and body placeholders, the mapping default templates and validations, and the local outputs reference (`AutomaticServiceReferencedNames`). The ordinary job's text-matching `FilterSnapshotContext` is not used. |
| Items | `WorkflowEngineService.AutomaticMultiInstanceServices.cs`: stage (only the referenced `config.*`/`setting.*`/`sys.*` names), render the request from the saved context with the existing `BuildStagedServiceInvocation` (now static), invoke through `IServiceTaskInvoker` with no transaction, map in memory with `MapAutomaticServiceItem`, save the candidate, then publish. An item with a saved candidate goes straight to publication; the job's result-ready row is never used. |
| Mapping | `MapAutomaticServiceItem` reuses `ResolveTypedOutputs` with the body's local outputs and writable shared aliases as targets. The status target receives the response's status before any validation runs. The candidate's validation context holds the names local and mapping validations read, because publication repeats, against current shared values, the validations of the mappings that filled their targets, from the response or a mapping default. The candidate records those targets (`MappedTargets`): a mapping the response left empty filled nothing, so its shared target keeps another writer's value and its local target keeps its output default, and neither is checked again. A candidate saved before `MappedTargets` existed repeats every mapping whose target holds a local value or a shared write. A number default whose text parses to infinity or NaN (`1e9999`, `NaN`) stays text and fails the type check, so it is a mapping failure. |
| Failure exits | Template, transport, timeout and non-2xx: `service_task_failed`, boundary selected by the response's status. Mapping and output contract: `multi_instance_result_invalid`, selected by the 2xx status. Validation failing only at publication: `multi_instance_result_invalid` with no status. Size refusals: `RefuseItemResultAsync`. Shared value change: `output_version_conflict` over the saved candidate. A statically read shared alias with no value: `job_conflict` incident. A REST item that fails its call or its mapping records a fixed description on the item and its node visit (`CaptureItemFailureAsync` takes it): "The REST call failed with HTTP status 404.", "The REST call got no HTTP response: …", or the 2xx mapping text. It names no request, response, or value. Size refusals and publication-time failures keep their generic texts. |
| Validation | Server (`FindItemExpressionProblem`) and editor (`automaticItemExpressionProblem`) reject `mi.*` names other than `mi.item`/`mi.index`/`mi.total` in a service body's URL, header values, body, mapping defaults, and mapping validations, with identical messages. Both quote a header by its trimmed name. |
| Editor | On a service body, the mapping **Variable** and **Status variable** fields suggest item-local outputs and readWrite shared aliases (numbers only for the status), rebuilt on focus. Help covers item templates, frozen credentials, idempotency through `${sys.jobId}`, a failed item's missing status, and the failures only a catch-all catches. The editor does not fill in the type of a mapping to an item-local output on import, so it refuses an untyped one with the server's message. |
| Documentation | [AGENTS.md](../../AGENTS.md), [runtime reference](../../Flowbit/README.md), [API reference](../api-guide.md#get-apiworkflowsauthoring-capabilities), [deployment](../deployment.md#automatic-multi-instance-foundation), [UI guide](../ui-guide.md), [TASK-05A](task-05a-multi-instance-core.md), [TASK-05C](task-05c-multi-instance-script-activities.md), the parent, and the backlog. |

### Review fixes (2026-11-13)

A review of the implementation found four defects, now fixed, and the missing tests
listed under Verification:

1. The editor gave an untyped mapping to an item-local output its output's type on
   import, so it saved what the server refused. The editor no longer does, and a parity
   test expects the same error on both sides.
2. A REST item that failed its call or its mapping recorded only the generic
   "Automatic activity item failed." The adapter now passes the fixed descriptions
   above, which reach the item and its node visit.
3. Publication validated every mapping that had a validation, including one whose
   optional path the response left empty, against the current shared value. Such an
   item failed with `multi_instance_result_invalid` for a value it never wrote.
   Publication now checks only the targets the item wrote. The new runtime test fails
   without this change. This covered shared targets only; the second review below
   found that local outputs were still counted.
4. The editor quoted a padded header name untrimmed, unlike the server.

### Second review fixes (2026-11-13)

A second review found two more defects, now fixed. Each new test failed on the code
before its fix:

1. A number default whose text parses to infinity or NaN (a mapping or local output
   default `${mi.item}` with the item `"1e9999"`, or a literal `"NaN"`) threw an
   `ArgumentException` while being serialized. Neither the adapter nor the job processor
   caught it, so a Worker retried the item, sending its request again on every attempt,
   until an incident opened; the boundary never received
   `multi_instance_result_invalid`. `CoerceDefaultScalar` now keeps such text as text,
   so the declared type check refuses it as an authored mapping failure, and
   `TypedOutputValueValidator.IsValidAuthoredDefault` refuses it at save, as the editor
   already did. The coercion is shared, so ordinary REST tasks, message events, and
   start, flow, and process variable defaults now return the same type error (400, or
   the boundary) instead of an unhandled failure.
2. Publication still repeated the validation of an optional mapping to a local output
   that the response left empty: the candidate's locals start from every output
   default, so the earlier fix counted that local as written. With a local default
   `"R-1"`, no `ref` in the response, and the rule `Len(optionalRef) > limit`, raising
   the shared `limit` during the call failed a successful item at publication. The
   candidate now records the targets its mappings filled (`MappedTargets`), and
   publication checks only those.

### Verification

- New PostgreSQL scenarios (`AutomaticMultiInstanceServiceRuntimeTests`, 21/21) publish
  through an API host with the switch open, run every job through the registered
  processor, and replace the REST invoker with a controlled receiver that records each
  request: a parallel body answered in reverse order (aggregate by index, each item's
  `Idempotency-Key` is its own job id, an instance edit after initialization never
  reaches the request); a sequential body (one request before the first item commits)
  and an empty collection (no request); an exact `404` boundary winning over the
  catch-all (partial result `completed`/`failed`/`cancelled`, error variable
  `service_task_failed`); an uncaught `503` retried with one job id, then an incident,
  and its retry resending only that item; a 2xx mapping failure taken by a `200–299`
  range boundary with `multi_instance_result_invalid`; a shared output conflict whose
  retry publishes the saved candidate with no new request; a validation failing only
  at publication taken by the catch-all, not the `201` boundary, with one request; an
  interrupting timer during the call (body cancelled, late response fenced); an entry
  failure a `5xx` boundary never takes; a statically read unset shared alias waiting in
  a `job_conflict` incident; and a setting rotated between attempts (the item's retry
  keeps the old value, the next item reads the new one, and settlement clears the
  staged input). The two reviews added: a failure with no response that only the
  catch-all takes; `retryFirst` with a status-matched boundary; a result too large to
  save; instance cancellation during the call; a non-interrupting timer; JSON object
  items; an optional shared mapping and an optional local mapping the response left
  empty, neither checked again at publication; a host outside Development; and a
  number mapping default rendering `1e9999`, taken by the catch-all with one request and
  no incident.
- Unit and parity tests (counted by test case): `AutomaticMultiInstanceServiceAdapterTests`
  (11, including four non-finite defaults and the recorded mapped targets) and
  `EditorAutomaticMultiInstanceServiceTests` (14, including a padded header name, an
  untyped item-local mapping, and four non-finite number defaults that both sides
  refuse at save). `MultiInstanceApiRegressionTests` adds two start-variable cases: a
  default template rendering `1e9999` or `NaN` returns `400` with
  `Variable 'amount' must be number.` instead of an unhandled failure. The tests that pinned service bodies as refused now pin call bodies only (`AutomaticMultiInstanceScriptAdapterTests`,
  `AutomaticMultiInstanceValuesTests`, `AutomaticMultiInstanceScriptRuntimeTests`), and
  `AutomaticMultiInstanceProcessorTests` pins that an item job naming a kind without an
  adapter opens a `job_invariant_violation` incident.
- Full solution after the second review fixes: **5,045 passed, 0 failed**, in 12 minutes
  51 seconds (5,032 plus the 13 new cases). Command:
  `dotnet test Flowbit/Flowbit.slnx --no-build -m:1`. The REST multi-instance classes
  and the new start cases alone: 48 passed. No editor code changed, so no browser run
  was repeated. Relative links and anchors in the changed documentation files resolve,
  and `git diff --check` is clean.
- Focused regression after the first review fixes: **2,242 passed, 0 failed**, in 6 minutes 6
  seconds (2,233 before them). Command:
  `dotnet test Flowbit/tests/Flowbit.Tests/Flowbit.Tests.csproj --no-build -m:1 --filter "FullyQualifiedName~AutomaticMultiInstance|FullyQualifiedName~Editor|FullyQualifiedName~Script|FullyQualifiedName~ServiceTask|FullyQualifiedName~SharedVariable|FullyQualifiedName~Durable|FullyQualifiedName~DefinitionValidation|FullyQualifiedName~WorkflowAuthoringPackage|FullyQualifiedName~CallActivity|FullyQualifiedName~Incident|FullyQualifiedName~Retry|FullyQualifiedName~ErrorBoundary"`.
- Full solution after the first review fixes: **5,032 passed, 0 failed**, in 13 minutes 10
  seconds (5,023 before them). Command: `dotnet test Flowbit/Flowbit.slnx --no-build -m:1`.
  Relative links and anchors in the ten changed documentation files resolve, and
  `git diff --check` is clean.
- Chromium smoke, editor classes only (`HostedEditorSmokeTests`, `EditorSmokeTests`,
  `AutomaticMultiInstanceEditorBrowserTests`), against Release-published hosts after the
  first review fixes: **60 passed, 0 failed**, in 3 minutes 23 seconds. One assertion in
  `AutomaticMultiInstanceEditorBrowserTests` still expected the help text from before
  TASK-05C and now expects the current text.
- First review fixes in a real browser (built-in Chromium pane, 1024×768): the editor folder
  served at `http://127.0.0.1:15800/flowbit-editor.html`, with a service body whose
  mapping to `notificationId` has no type and whose `Idempotency-Key` header name is
  padded with spaces and a no-break space. In standalone mode, **File → Save JSON** listed
  the server's mapping-type error. In a hosted-mode mount with the capability open (set
  up from script; the clicks and typing were real), **Save new version** listed the
  mapping-type error and the header error quoting `Idempotency-Key` trimmed. The
  inspector showed the mapping's **Data type** as `json`; choosing `string` and typing
  `flowbit-supplier-${sys.jobId}` into the header value made the save pass. The console
  had no messages.
- Real browser (built-in Chromium pane) against Release-published API
  (`http://127.0.0.1:15717`, Development, switch open), Worker, and UI
  (`http://127.0.0.1:15752`) over a disposable PostgreSQL 17, with a local Node receiver
  (`http://127.0.0.1:15690`) that answers `201`, answers `404` for `missing`, and holds
  `slow` until released. At 1440×900 in the hosted editor: imported a parallel service
  body, saw its multi-instance marker, the mapping **Variable** and **Status variable**
  suggestions listing only item-local outputs (numbers only for the status), typed the
  status target in lower case and saw it take the declared name, typed
  `/${mi.completed}` into the URL and got the new save error, restored it with Ctrl+Z,
  read the REST, status and error-boundary help, saved, published and started. The
  Worker completed the body in about three seconds: the receiver got three requests
  with keys `flowbit-supplier-3`/`4`/`5`, and instance detail showed the ordered
  aggregate and each item's local outputs, with no request or staged value in item
  detail. Edited the collection to `supplier-a, missing, slow`, saved version 2,
  published and started: the `404` boundary took the body (error variable
  `service_task_failed`, all three rows `failed` or `cancelled`, because the `404` was
  captured before item 0 saved its result), the token ended at **Supplier not found**,
  and the Worker cancelled the held `slow` call when its fence was lost; releasing it
  changed no variable, token, or job and opened no incident. Instance detail and the
  service inspector had no page overflow at 1440×900, 1024×768, and 390×844 (the item
  tables scroll inside their panels at 390). The console had no errors or warnings, and
  the API, UI, and Worker logged no errors.
- Scale run on that stack: a parallel body of 1,000 items (`maxConcurrency` 50, one
  Worker with default limits, so at most 4 items of the instance at once) completed in
  **225 seconds** (about 4.4 items per second), with 1,000 requests, 1,000 distinct
  idempotency keys, no incidents, and 1,000 dispatch control jobs (750 superseded). The
  Development hosts logged every SQL command at Information, which slows them; queries
  per item were not measured. The final click on **Start instance** for this run was
  sent from script because the pane had stopped drawing; the other two starts were
  real clicks.

### Still open

- Worker-enabled browser acceptance scenarios for service bodies in
  `Flowbit/tests/Flowbit.BrowserAcceptanceTests/`, with screenshots; the manual browser
  check above does not replace them. Of the ordinary Chromium smoke suite, only the
  editor classes were run.
- A scale run with production log levels and query counts per item. The run above
  shows one dispatch control job enqueued per item publication, which TASK-05A lists
  under per-item cost.
- Tests the plan lists that do not exist yet: a conditional boundary (either kind) on a
  service body, a shared typed collection's success path, several item incidents at
  once, a response body over `WorkflowServiceTasks.MaxResponseBodyBytes` (the
  controlled receiver replaces the invoker that enforces it), a crash between the call
  and the candidate save (resend with the same `sys.jobId`), and multiple Workers under
  queue pressure.
- Manual browser steps 3–5 of §7 (incident retry through the UI, held responses with
  cancellation and conflicts, paging and permission states), and screenshots of the
  queued, running, blocked, completed, and cancelled screens.
- The prepared REST sections of the owning guides and the canonical REST examples,
  merged in the parent's integration change (§8). Until then the definitions above live
  in the tests.
