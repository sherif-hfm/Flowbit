# TASK-05B: Multi-instance REST service activities

[Parent TASK-05](task-05-multi-instance-automatic-activities.md) · [Shared core TASK-05A](task-05a-multi-instance-core.md) · [Enhancement backlog](README.md) · [Documentation home](../index.md)

| Field | Value |
| --- | --- |
| Status | Planned |
| Category | New task type |
| Effort | M |
| Priority | Not ranked |
| Parent | [TASK-05](task-05-multi-instance-automatic-activities.md) |
| Depends on | [TASK-05A](task-05a-multi-instance-core.md) |
| Related | [EVENT-05](event-05-error-code-boundaries.md) |

## Summary

Implement the REST `serviceTask` adapter for automatic multi-instance execution: process an instance array in parallel or sequentially, invoke one durable HTTP operation per admitted item, and collect validated local outputs in the ordered aggregate owned by TASK-05A.

This is a proposed implementation plan, checked against the repository on **2026-11-11**. Automatic multi-instance service tasks are not implemented. This planning change does not claim runtime or browser verification.

This subtask is one part of TASK-05's first release. Completing REST support does not complete the parent: script and call activities remain required in that same release. TASK-05A is authoritative for the shared JSON schema, item lifecycle, persistence, admission, aggregation, read APIs, authorization, and rollout; this page specifies the REST behavior and integration work.

## First release acceptance criteria

- [ ] A REST `serviceTask` with `multiInstance.source: "collection"` and `asyncBefore: true` uses the core coordinator in both sequential and parallel modes. Body entry never makes an extra ordinary service invocation.
- [ ] Every admitted item owns one durable activity job with stable item/job identity, frozen inputs, existing HTTP timeout/response limits, and the core's item/lease fences. Empty input makes no HTTP request.
- [ ] Exact-name templates can read `mi.item`, zero-based `mi.index`, `mi.total`, frozen instance inputs, and the `config.*`/`setting.*` values and clock read when the item first stages. JSON items can be sent whole; no dotted item-path lookup is introduced.
- [ ] `service.outputMappings` and `service.statusVariable` write declared item locals only. Mapping/default/type/validation failures cannot partially publish locals or process variables.
- [ ] REST failures preserve existing exact/range/catch-all boundary matching and `boundaryFirst`/`retryFirst` behavior. An exhausted uncaught failure opens an incident while the body waits. A caught failure publishes the core's partial result before the boundary continues.
- [ ] Retry, restart, queue-capacity recovery, and aggregate recovery do not invoke a committed successful item again. At-least-once external effects and authored idempotency keys are documented accurately.
- [ ] Body cancellation or a caught failure cancels/fences unfinished work. A late HTTP response cannot commit locals, advance the parent, or overwrite its aggregate.
- [ ] Editor authoring, import/export, validation, markers, runtime item detail, and authorized incident navigation work without changing ordinary REST tasks or human MI behavior.
- [ ] REST-specific automated tests, controlled HTTP integration scenarios, canonical examples, documentation, and real-browser evidence satisfy the completion handoff below.

## Scope, non-goals, and current evidence

Support only the existing REST connector on `serviceTask`. The body keeps one unconditional outgoing flow. `asyncBefore: true` is required; optional `asyncAfter` applies once after aggregate publication. Existing error boundaries act on the whole body. The core rejects timer/conditional boundaries on the body, cardinality, early completion, manual item actions, shared-variable access in item execution, and direct per-item process writes.

This subtask does not add connectors, an expression-based HTTP template language, compensation, selective retry-all/skip endpoints, per-item boundaries, or a physical exactly-once guarantee. It does not duplicate the coordinator, item tables, capacity reservation, aggregate completion job, or common runtime screens owned by TASK-05A.

Current implementation evidence:

- `WorkflowEngineService.Jobs.cs` already implements `StageAndInvokeServiceJobAsync`, `BuildStagedServiceInvocation`, `RestoreStagedServiceInvocation`, `ApplyStagedServiceResultAsync`, and `ShouldRetryBeforeBoundary`. These are the starting points for factoring REST mechanics.
- Ordinary durable service finalization writes process outputs and advances a token whose single `WaitingJobId` owns the job. An MI item instead settles through the core's item fence and local-output contract; widening ordinary job checks is insufficient.
- `ServiceTaskTemplating` performs exact dictionary lookup. URL/header substitution is strict; body substitution has permissive missing-value behavior that cannot be used to hide an unsupported shared reference.
- `HttpServiceTaskInvoker` already supplies the HTTP execution boundary. Preserve its established request, timeout, cancellation, response-size, and error behavior instead of introducing an MI HTTP stack.
- `WorkflowModelMigrator`, editor normalization, and validation currently restrict MI to human tasks. The core owns the common change; this adapter supplies REST-specific validation and import/authoring cases.

## Plan

### 1. Integrate through the shared core

Agree the adapter boundary with TASK-05A before changing ordinary REST execution. The core supplies the immutable body context, copied item/index/total, captured causal actor/allowlisted claims, local declarations/defaults, stable job identity, and eligible item/parent state. The REST adapter stages an invocation and returns a bounded successful local-output map or the existing classified failure outcome. It must not route the token or call process-variable persistence for item outputs.

| Responsibility | Owner and integration rule |
| --- | --- |
| Entry and item jobs | TASK-05A transfers the parent `asyncBefore` job into the body wait and admits `multiInstanceItem` activity jobs. REST entry must not call `ExecuteServiceTaskAsync` once before fan-out. |
| Staging/invocation | This adapter reuses existing REST template construction and invoker mechanics against the frozen item context. |
| Mapping/failure outcome | This adapter validates local output/status values and returns success or the existing service failure classification, including eligible HTTP status. |
| Item persistence and refill | TASK-05A atomically settles the item, counts/ledger, and durable dispatch/completion signal. Capacity contention cannot undo accepted success and trigger another HTTP invocation. |
| Aggregate and continuation | TASK-05A alone resets/publishes `resultVariable`, fences concurrent writes, processes variable notifications, and performs the single outgoing/async-after continuation. |
| Limits, version changes, retention | Use the core's captured concurrency, item/snapshot/result bounds, active-work retention, and open-body version blocker. Do not add REST-only competing settings or lifecycle state. |

The common schema/migration and core runtime must be present before this adapter runs. Do not ship a REST-only feature gate that allows incomplete cancellation/recovery or suggests the parent release is finished without calls.

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

Instance inputs and the item come from the body snapshot taken at successful body initialization, after the parent async-before wait. Template `config.*`/`setting.*` values and `sys.now`/`sys.today` are read when the item first stages, as an ordinary durable REST job reads them at its first attempt, and kept in the item's staged snapshot for its retries. A credential rotated before an item stages therefore reaches that item; later process edits and sibling completion never change an item's inputs. Preserve existing job/attempt metadata behavior; `sys.jobId` stays stable across retries of that item.

Apply these rules consistently in the server, editor save validator, live field validation, and runtime guard:

1. Reuse strict URL/header substitution, JSON-body substitution and validation, HTTP method/header rules, and existing staged-invocation restoration. Keep exact-name `mi.item`, `mi.index`, and `mi.total` available in the item context. Progress counters such as `mi.completed`/`mi.remaining` are not inputs to item requests.
2. Reject explicit shared-alias dependencies in URL/header/body placeholders, mapping defaults/validations, local validation, and aggregate/boundary-target validation. Guard unsupported references before runtime body substitution: removing a shared alias from the dictionary must not silently send JSON null. Unrelated nodes may continue using shared variables.
3. Resolve `outputMappings[].variable` against local declarations. Reuse path extraction, required/default behavior, type/array checks, validation order, and the ordinary all-or-nothing mapping batch. A mapping may not target a process variable, shared alias, undeclared name, reserved context name, or aggregate target.
4. Resolve `statusVariable` against a declared scalar-number local. It cannot overlap another output mapping. Validate status storage with the same local contract as mapped values; it does not become a process producer.
5. Apply defaults to a fresh local buffer for an attempt and validate the final overlay before accepting success. Every declared output must satisfy the core's completion contract; nullable unset locals become JSON null, and an empty declaration list yields `{}` for a side-effect-only request.
6. A failed attempt discards staged locals. A service failure may retain its status in bounded item diagnostics, but does not commit that value into a process variable. Only the selected whole-body boundary may write its normal declared instance error target through core routing.

Factor pure request construction and result-to-local mapping from process writes. Keep ordinary `ApplyStagedServiceResultAsync` semantics intact for non-MI work, including ordinary shared status/output handling. Update producer inference so MI local mappings/status are excluded from the process producer catalog, while ordinary service producers remain discoverable by calls and validation.

### 4. Durable execution, retries, and cancellation

The core owns the transaction/lock order. REST follows its staged protocol:

| Phase | REST-specific behavior |
| --- | --- |
| Stage | Verify body/item/job/lease ownership, create the request from frozen inputs, persist the bounded invocation snapshot, then commit and discard tracked runtime state. |
| Invoke | Call the existing HTTP invoker without holding instance, item, or shared row locks. Honor cancellation and existing timeout/response limits. |
| Persist response | Use existing durable result-ready behavior so a recovered finalize phase can consume the saved response without another HTTP request. |
| Finalize | Reacquire and refresh runtime state, verify the core's exact item/activation/lease fence, validate local outputs, and settle once. Do not clear the parent's wait or route it per item. |
| Failure | Return the existing classified service outcome to the common retry/boundary/incident flow; retain item/job identity and the frozen request inputs. |

Preserve `boundaryFirst` as the existing default. With a matching boundary, it catches the first applicable failure; `retryFirst` retries before taking that boundary after exhaustion. With no matching boundary, apply the normal retry schedule and then open the item's incident. Preserve exact HTTP-status, status-range, and catch-all selection, including non-HTTP failures such as timeout and mapping validation. Do not treat a transient response as successful merely because its body can be mapped.

A caught item failure interrupts the entire body once. The core keeps completed siblings, faults the winning visit, cancels/fences unfinished jobs and pending items, publishes its partial result, and routes the boundary. An uncaught incident occupies its slot and pauses new admission according to the core; already admitted siblings can settle. An operator retry resumes the same failed item job, never successful siblings. The recoverable node visit stays active; terminal ledger state is not reopened. A response that repeats on every attempt, such as a 4xx the receiver always returns for one item, therefore keeps an uncaught body waiting until the instance is cancelled. This is the core's documented first-release limit; the guides should recommend a boundary for batches that must finish.

Instance/scope cancellation, termination, parent-call cancellation, or a winning error boundary invalidates item publication immediately. A cancelled request may still finish externally. Its late result must fail the refreshed item/body fence without local writes, aggregate writes, audit continuation, or new admission. Cancellation cannot compensate remote effects.

REST transport is **at least once**. A request can succeed remotely before its response is durably recorded, or an old request can outlive a lost lease. A replacement attempt may therefore reach the receiver again. Recommend authored receiver-supported idempotency headers based on stable `sys.jobId`, as in the example; do not vary that key by retry attempt. A new body visit creates new item/job identities. The core's strict logical admission bound does not promise a physical maximum of remote requests still running after lease loss.

Aggregate version conflicts are coordinator incidents over saved locals, and deterministic aggregate failures take the boundary or fault the instance as the core specifies. Neither path may clear successful REST item results or make additional successful HTTP calls. A full queue likewise uses the core's reserved dispatch/completion path, not an adapter retry that re-invokes remote work.

### 5. Validation, editor, and operational UI

Add REST-specific rules to the shared automatic-MI validation owner: required async-before, one ordinary outgoing flow, local-only output/status targets, shared dependency rejection, and permitted whole-body error boundaries. Existing malformed imported settings must remain visible until explicitly edited/removed. Do not normalize unsupported MI input into an ordinary synchronous task.

In the canonical editor, expose the core MI section on service tasks. Enabling it enables async-before and reviews existing output mappings/status targets; require valid locals rather than silently redirecting existing process writes. Disabling MI or converting the node type must surface/confirm incompatible settings and must not silently promote locals to process producers. Integrate accepted changes with existing undo/history handling and `changeNodeTypeFromInspector`.

Show local declarations and distinguish mapping/status local targets from process variables. Help must explain frozen instance inputs, settings and clock read when each item first runs, exact-name item templates, optional once-per-body async-after, Worker dependency, retry/boundary behavior, partial results on a caught failure, and idempotency. Render sequential/parallel MI markers on the service shape; preserve accessible labels, field help, imported diagnostics, hosted dirty/saved state, and AI snapshot/apply behavior. No new editor dependency is required.

Use TASK-05A's paged automatic-item views, execution kind, read routes, and Operations authorization. Add REST status/failure and stable job correlation where appropriate; no new complete-item or retry-all API is introduced. Ordinary instance visibility does not confer job-retry authority. Do not expose staged authorization headers, full HTTP requests, credentials, or snapshot internals in item DTOs, logs, metrics, or UI. Bounded diagnostics must follow the existing redaction contract.

### 6. File ownership and implementation sequence

Paths below are relative to the repository root. New adapter filenames, if needed, are implementation proposals rather than existing APIs.

| Area | Principal files and boundary |
| --- | --- |
| REST job adapter | `Flowbit/src/Flowbit.Service/Services/WorkflowEngineService.Jobs.cs`, relevant service methods in `WorkflowEngineService.cs`, and the automatic-MI coordinator seam supplied by TASK-05A. Factor REST mechanics without copying the coordinator. |
| HTTP/templates | `Flowbit/src/Flowbit.Service/Services/ServiceTaskTemplating.cs`, `Flowbit/src/Flowbit.Infrastructure/Http/HttpServiceTaskInvoker.cs`; preserve ordinary behavior and change only where shared factoring or precise guards require it. |
| Rules/models | `WorkflowDefinitionValidator.cs`, the core automatic-MI rule helper, `Flowbit.Shared/Models/WorkflowModel.cs`, `WorkflowModelMigrator.cs`, `CallActivityRules.SourceDeclarations`, shared-access/readiness analysis, and existing typed-output/error-boundary helpers. Coordinate shared-file edits with TASK-05A. |
| Editor/UI | `Flowbit/src/Flowbit.Ui/wwwroot/editor/flowbit-editor.js`; template/CSS only as required. Integrate REST presentation with core instance item views and Operations correlation. |
| Tests/examples | `Flowbit/tests/Flowbit.Tests/`, the separate browser suites, and canonical examples under `examples/multi-instance/`. |

1. **Characterize ordinary REST behavior.** Pin template substitution, mapping/status validation, boundary selection, and staged response recovery. Agree the core adapter input/outcome contract before refactoring.
2. **Implement REST validation and staging.** Accept the shared MI schema only through the core path; add local-only mapping/status validation and frozen-input request construction. Gate on API/editor parity and ordinary REST regression tests.
3. **Integrate durable invocation and local finalization.** Attach the existing invoker/result-ready pipeline to item ownership. Gate on both modes, out-of-order results, no extra entry request, and empty-input behavior.
4. **Integrate failure and recovery.** Exercise retry precedence, status matching, incident recovery, lost leases, late responses, queue pressure, and aggregate conflict recovery. Prove successful request counts do not increase during unrelated recovery.
5. **Complete authoring and operations UX.** Add controls/help/markers and REST-specific item diagnostics through the core screens. Exercise both standalone and hosted editors and Worker-enabled runtime behavior in real browsers.
6. **Deliver documentation/examples and evidence.** Update affected owning guides, run the shared release gates with the other adapters, and hand results to TASK-05. Do not add a separate schema or rollout path for REST alone.

### 7. Automated and real-browser verification

Add focused REST MI cases under `Flowbit/tests/Flowbit.Tests`; proposed scenario groups are not claims that new tests already exist.

| Group | Required evidence |
| --- | --- |
| Definition/editor parity | Required async-before; both modes; local output/status types/collisions; shared URL/header/body/default/validation references rejected; wrong/missing/null settings; imported invalid values survive redraw and round-trip. Ordinary REST/human MI behavior stays unchanged. |
| Templates and mappings | Scalar and JSON whole-item bodies, exact item/index/total names, scalar URL/header rules, escaping and JSON validation, no implicit item paths, frozen inputs after process edits, settings and `sys.now` read at item staging and kept across retries (a rotated setting reaches later items), mapping defaults/required/null/array/type rules, final local completeness, empty locals, and all-or-nothing mapping/status results. |
| Durable success and admission | Parallel responses complete in reverse order but aggregate by index; sequential sends no next request before prior item success commits; empty array sends none; duplicate inputs remain separate items; multiple Workers and queue pressure respect core admission. |
| Failure/recovery | Exact/range/catch-all/no-match; timeout/transport/invalid response/mapping/status failures; boundary-first versus retry-first; partial result published on a caught failure; exhausted incidents and same-item retry; several incidents; successful siblings stay untouched. |
| Crashes and side effects | Gates around stage, remote effect, response persistence, result-ready, local commit, and aggregate commit; restart and lease loss preserve stable idempotency headers and single local completion. Prove saved-response finalization and committed-success recovery make no extra HTTP request. |
| Cancellation and conflicts | Held request versus body/instance/scoped/parent-call cancellation or sibling boundary; late response discarded; simultaneous final successes; aggregate output conflict retries only coordinator; conditional work triggered by reset/publication cannot admit or route after cancellation. |
| Limits/security | Core item/snapshot/result limits plus HTTP response bounds; no truncation; shared-template runtime guard before substitution; no secrets in DTOs/logs; denied job inspection/retry; automatic items absent from human inbox/actions. |

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

1. Create/import parallel and sequential service bodies; configure locals/status/mappings, save/reopen, inspect malformed inputs, change type, and undo/redo in standalone and hosted editors.
2. Start with the Worker stopped, inspect queued state, start it, and inspect ordered results while the receiver releases responses in reverse order. Exercise empty input.
3. Cause a real transient failure and exhausted incident; inspect the authorized Operations link, retry through the UI/keyboard, and verify successful sibling call counts do not increase.
4. Hold a response, cancel the body/instance, release it, and verify cancelled evidence, no late aggregate/continuation, and no accidental normal flow. Exercise a matching boundary and inspect its partial result, then an aggregate-publication conflict.
5. Check paged item results, loading/error states, identity-change stale responses, denied detail/retry, and the open-body version-switch blocker without changing human MI screens.

Inspect console errors/warnings and host logs. Capture the service MI controls/marker and queued, running, blocked, completed, and cancelled runtime screens. Record browser/version, exact localhost URLs, interactions, viewport sizes, console results, screenshots, and unavailable checks. Unit/Jint/DOM-stub tests do not replace browser verification.

### 8. Documentation and completion handoff

Update current-behavior guides when implementation lands, coordinating common sections with TASK-05A. This planning change creates only this subtask plan.

| Owning document | REST-specific implementation update |
| --- | --- |
| [BPMN support](../bpmn-support.md), [node/property reference](../node-reference.md) | Supported REST MI subset, required async-before/optional body async-after, exact templates, local output/status rules, whole-body boundaries, and deferred features. |
| [Developer guide](../developer-guide.md) | HTTP-only walkthroughs, snapshot timing and per-item settings, ordered aggregate, receiver-supported idempotency, retry/boundary differences, partial results, the stuck-body limit, cancellation, and aggregate recovery without repeated successes. |
| [API reference](../api-guide.md), [Flowbit.Ui guide](../ui-guide.md) | REST item/status/job correlation through the core contract; authoring controls, permissions, paged results, incident recovery, and changed screenshots. |
| [Deployment/operations](../deployment.md), [runtime reference](../../Flowbit/README.md), [AGENTS.md](../../AGENTS.md) | Staging/unlocked invocation/fenced local settlement, limits, Worker/capacity interaction, redaction, and recovery. Shared migrations/settings/rollout remain owned by TASK-05A. |
| [Example catalog](../../examples/README.md), [MI examples](../../examples/multi-instance/README.md) | Controlled REST examples in both modes, object-item body, side-effect-only outputs, empty collection, error boundary, and retry. State inputs, expected outputs/call counts, Worker/endpoint prerequisites, and idempotency support. |

Review [database schema](../database-schema.md) only if adapter integration changes core persistence, and return those changes to TASK-05A rather than documenting an independent model. Update root capabilities, setup instructions, or documentation links only when affected. Validate relative links/anchors/images and parse JSON fragments; exercise changed example walkthroughs over HTTP with equivalent PowerShell/Bash commands. Do not change AI tool names, descriptions, or instructions.

Before marking TASK-05B Done, attach actual automated and browser evidence here, confirm ordinary REST and human MI compatibility, and return the final adapter contract, example inputs/results, error/retry matrix, and any shared-core changes to TASK-05. Follow the parent's coordinated migration/publication/rollback plan. REST implementation is complete only when its required core integration and recovery checks pass; the parent remains open until script and call subtasks and the joint first-release gates are also complete.
