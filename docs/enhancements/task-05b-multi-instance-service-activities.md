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

Implement the REST `serviceTask` adapter for automatic multi-instance execution: process an instance or shared typed array in parallel or sequentially, invoke one durable HTTP operation per admitted item, and collect validated local outputs in the ordered aggregate owned by TASK-05A. The first release includes shared reads/writes and interrupting or non-interrupting timer/conditional boundaries on the body.

This is a proposed implementation plan, checked against the repository on **2026-11-11**. Automatic multi-instance service tasks are not implemented. This planning change does not claim runtime or browser verification.

This subtask is one part of TASK-05's first release. Completing REST support does not complete the parent: script and call activities remain required in that same release. TASK-05A is authoritative for the shared JSON schema, item lifecycle, persistence, admission, aggregation, read APIs, authorization, and rollout; this page specifies the REST behavior and integration work.

## First release acceptance criteria

- [ ] A REST `serviceTask` with `multiInstance.source: "collection"` and `asyncBefore: true` uses the core coordinator in both sequential and parallel modes. Body entry never makes an extra ordinary service invocation.
- [ ] Every admitted item owns one durable activity job with stable item/job identity, frozen inputs, existing HTTP timeout/response limits, and the core's item/lease fences. Empty input makes no HTTP request.
- [ ] Exact-name templates can read `mi.item`, zero-based `mi.index`, `mi.total`, frozen instance inputs, and declared shared aliases plus `config.*`/`setting.*` values and clock captured when the item first stages. JSON items can be sent whole; no dotted item-path lookup is introduced.
- [ ] `service.outputMappings` and `service.statusVariable` target declared item locals or declared `readWrite` shared aliases. The core atomically commits a successful item's shared write batch, local results, and completion under shared value-version fences; mapping/default/type/validation failures publish no partial batch.
- [ ] REST failures preserve existing exact/range/catch-all boundary matching and `boundaryFirst`/`retryFirst` behavior. An exhausted uncaught failure opens an incident while the body waits. A caught failure publishes the core's partial result before the boundary continues.
- [ ] Retry, restart, queue-capacity recovery, and shared-item/aggregate publication recovery do not invoke a successfully executed item again once its response/result is durably saved. At-least-once external effects and authored idempotency keys are documented accurately.
- [ ] Body cancellation, an interrupting timer/conditional boundary, or a caught failure cancels/fences unfinished work. A late HTTP response cannot commit locals or shared writes, advance the parent, or overwrite its aggregate. Non-interrupting timer/conditional boundaries leave the body running and create their own continuation under the core's subscription/firing rules.
- [ ] Editor authoring, import/export, validation, markers, runtime item detail, and authorized incident navigation work without changing ordinary REST tasks or human MI behavior.
- [ ] Until the parent's integration step, service bodies publish and run only on a Development host with the release switch open. Script and call bodies stay rejected until their own adapters land, and every other environment rejects all three (§1).
- [ ] REST-specific automated tests, controlled HTTP integration scenarios, canonical examples, documentation, and real-browser evidence satisfy the completion handoff below.

## Scope, non-goals, and current evidence

Support only the existing REST connector on `serviceTask`. The body keeps one unconditional outgoing flow. `asyncBefore: true` is required; optional `asyncAfter` applies once after aggregate publication. Error, timer, and conditional boundaries attach to the body, never individual items. Timer/conditional boundaries support both interrupting and non-interrupting behavior through TASK-05A; conditional predicates keep the existing instance-only contract and shared writes do not create conditional wakes. The core rejects cardinality, early completion, manual item actions, and direct per-item writes to ordinary instance variables. Shared collection sources, staged shared reads, permitted shared targets, and shared aggregate/error targets are first-release work.

This subtask does not add connectors, an expression-based HTTP template language, compensation, selective retry-all/skip endpoints, per-item boundaries, or a physical exactly-once guarantee. It does not duplicate the coordinator, item tables, capacity reservation, aggregate completion job, or common runtime screens owned by TASK-05A.

Current implementation evidence:

- `WorkflowEngineService.Jobs.cs` already implements `StageAndInvokeServiceJobAsync`, `BuildStagedServiceInvocation`, `RestoreStagedServiceInvocation`, `ApplyStagedServiceResultAsync`, and `ShouldRetryBeforeBoundary`. These are the starting points for factoring REST mechanics.
- Ordinary durable service finalization writes process outputs and advances a token whose single `WaitingJobId` owns the job. An MI item instead settles through the core's item fence and local-output contract; widening ordinary job checks is insufficient.
- `ServiceTaskTemplating` performs exact dictionary lookup. URL/header substitution is strict; body substitution has permissive missing-value behavior. Resolve declared shared bindings during staging and fail an unavailable/invalid binding explicitly rather than silently substituting JSON null.
- Ordinary `WorkflowJobRepository` incident retry clears the saved result and, for `output_version_conflict`, the staging snapshot. MI publication retry needs a separate branch over retained item candidates; reusing that path unchanged would repeat an already successful HTTP request.
- `HttpServiceTaskInvoker` already supplies the HTTP execution boundary. Preserve its established request, timeout, cancellation, response-size, and error behavior instead of introducing an MI HTTP stack.
- `WorkflowModelMigrator`, editor normalization, and validation currently restrict MI to human tasks. The core owns the common change; this adapter supplies REST-specific validation and import/authoring cases.

## Plan

### 1. Integrate through the shared core

Agree the adapter boundary with TASK-05A before changing ordinary REST execution. The core supplies the immutable body context, copied item/index/total, captured causal actor/allowlisted claims, local declarations/defaults, shared access plan, stable job identity, and eligible item/parent state. The REST adapter stages an invocation and returns a bounded candidate containing local results and shared writes, or the existing classified failure outcome. The coordinator owns all persistence and publication fences; the adapter never routes the token or writes runtime variables itself.

| Responsibility | Owner and integration rule |
| --- | --- |
| Entry and item jobs | TASK-05A's `InitializeBodyAsync` transfers the parent `asyncBefore` job into the body wait and its dispatch admits `multiInstanceItem` activity jobs. Calling it from the entry job and executing an item job are adapter work (see **Release boundary** below). REST entry must not call `ExecuteServiceTaskAsync` once before fan-out. |
| Staging/invocation | This adapter reuses existing REST template construction and invoker mechanics against frozen body inputs and the item's saved shared/settings/clock context. |
| Mapping/failure outcome | This adapter validates local/shared output and status candidates and returns success or the existing service failure classification, including eligible HTTP status. |
| Item persistence and refill | TASK-05A checks shared `ValueRevision` fences and atomically commits the validated shared batch, locals, item/counts/ledger, and durable dispatch/completion signal. Capacity contention cannot undo accepted success and trigger another HTTP invocation. |
| Aggregate and continuation | TASK-05A alone resets/publishes `resultVariable`, fences concurrent writes, processes variable notifications, and performs the single outgoing/async-after continuation. |
| Limits, version changes, retention | Use the core's captured concurrency, item/snapshot/result bounds, active-work retention, and open-body version blocker. Do not add REST-only competing settings or lifecycle state. |

The common schema/migration and core runtime must be present before this adapter runs. Do not ship a REST-only feature gate that allows incomplete cancellation/recovery or suggests the parent release is finished without calls.

**Release boundary.** The foundation's publication, runtime, and item-job guards are unconditional, so no service body can be published or run today, even on a Development host. Open them for service bodies only, as [TASK-05A §8](task-05a-multi-instance-core.md#8-definition-validation-compatibility-and-safety-of-loading) records under **Opening by activity kind**:

- A service body publishes only when the release switch is enabled, which only a Development host allows, and every automatic body in its definition has an installed adapter. This is not the REST-only gate ruled out above: no other environment can open it, and the integration change removes it.
- The service adapter counts as installed only in the change that completes its entry, item, failure, and cancellation paths.
- Routing the body's `asyncBefore` entry job to `InitializeBodyAsync` and its `multiInstanceItem` jobs to this adapter is adapter work in `WorkflowEngineService.Jobs.cs`, shared with [TASK-05C](task-05c-multi-instance-script-activities.md). Whichever lands first adds one dispatch by node type; the other adds its case.
- Tests pin that script and call bodies stay refused at publication, entry, and item processing until their adapters land, and that a host outside Development refuses service bodies whatever its options say.

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

Apply these rules consistently in the server, editor save validator, live field validation, and runtime guard:

1. Reuse strict URL/header substitution, JSON-body substitution and validation, HTTP method/header rules, and existing staged-invocation restoration. Keep exact-name `mi.item`, `mi.index`, and `mi.total` available in the item context. Progress counters such as `mi.completed`/`mi.remaining` are not inputs to item requests.
2. Include declared readable shared aliases used by URL/header/body placeholders, defaults, and validations in the core's staged shared-access plan. Preserve catalog binding/type/access checks, canonical key ordering, and current missing/null semantics. An invalid, missing, or archived binding fails explicitly before invocation; ordinary legitimate nullable values keep their contract. Aggregate and boundary validation use the core's publication context and access plan.
3. Resolve `outputMappings[].variable` against local declarations or declared `readWrite` shared aliases. Reuse path extraction, required/default behavior, type/array checks, validation order, and the ordinary all-or-nothing mapping batch. A mapping may not target an ordinary instance variable, read-only shared alias, undeclared name, reserved context name, or aggregate target. Enforce the core's alias/canonical-key collision rules, including its separation of collection, aggregate, item-write, and error targets.
4. Resolve `statusVariable` against a declared scalar-number local or `readWrite` shared alias. It cannot overlap another output mapping by alias or canonical shared key. Local status is not a process producer; a shared status target is an explicit shared producer in the access and lock-order plans.
5. Apply defaults to a fresh local buffer for an execution/mapping attempt and validate the final overlay before accepting success. Publication-only retry retains the saved candidate instead. Every declared output must satisfy the core's completion contract; nullable unset locals become JSON null, and an empty declaration list yields `{}` for a side-effect-only request.
6. A failed execution or mapping attempt discards both locals and staged shared writes; a publication conflict retains its successful candidate. A service failure may retain its status in bounded item diagnostics, but publishes neither a local success nor a shared status/mapping batch. The selected whole-body error boundary may write its declared instance or `readWrite` shared error target through the core's fenced boundary publication.

Factor pure request construction and result-to-candidate mapping from persistence. Keep ordinary `ApplyStagedServiceResultAsync` semantics intact for non-MI work, including ordinary shared status/output handling. Exclude MI locals from process-producer inference while retaining declared shared targets and their validation dependencies in access/readiness/lock-order analysis. The aggregate contains local results; shared writes retain their normal catalog/audit evidence and bounded value-free item correlations, not invented local declarations or copied values in ordinary instance history. Include shared input and candidate data in the core's combined 1 MiB staged-payload limit and retain candidates independently of completed job cleanup.

### 4. Durable execution, retries, and cancellation

The core owns the transaction/lock order. REST follows its staged protocol:

| Phase | REST-specific behavior |
| --- | --- |
| Stage | Verify body/item/job/lease ownership, read shared aliases and capture target `ValueRevision` stamps under canonical shared-key locks after runtime locks, create the request, persist the bounded immutable invocation/input/fence snapshot, then commit and discard tracked runtime state. |
| Invoke | Call the existing HTTP invoker without holding instance, item, or shared row locks. Honor cancellation and existing timeout/response limits. |
| Persist response | Use existing durable result-ready behavior so a recovered finalize phase can consume the saved response without another HTTP request. |
| Finalize | Reacquire fresh runtime and canonical shared-key locks, verify item/activation/lease and output-value fences, validate the saved local/shared candidate against its contracts, and ask the core to commit the whole batch and item success once. Do not clear the parent's wait or route it per item. |
| Shared publication conflict | Retain response, candidate values, frozen inputs, and item/job identity. Open the core publication incident and retry only fenced publication after authorized fence refresh; never repeat HTTP invocation to resolve this conflict. |
| Failure | Return the existing classified service outcome to the common retry/boundary/incident flow; retain item/job identity and the frozen request inputs. |

Preserve `boundaryFirst` as the existing default. With a matching error boundary, it catches the first applicable failure; `retryFirst` retries before taking that boundary after exhaustion. With no matching boundary, apply the normal retry schedule and then open the item's incident. Preserve exact HTTP-status, status-range, and catch-all selection, including non-HTTP failures such as timeout and an authored candidate violating a valid target value contract. Missing/archived bindings, binding access or contract drift, and `ValueRevision` conflicts are operational incidents, not business failures caught by an error boundary. Once work has produced a saved response/candidate, recovery of those publication failures retains it and never invokes HTTP again; before invocation, the staging incident can retry only after its binding problem is resolved. Do not treat a transient response as successful merely because its body can be mapped.

A caught item failure interrupts the entire body once. The core keeps completed siblings, faults the winning visit, cancels/fences unfinished jobs and pending items, publishes its partial result, and routes the boundary. An uncaught incident occupies its slot and pauses new admission according to the core; already admitted siblings can settle. An execution retry resumes the same failed item job, never successful siblings; publication recovery retains its saved candidate. The recoverable node visit stays active; terminal ledger state is not reopened. A response that repeats on every attempt, such as a 4xx the receiver always returns for one item, keeps an uncaught body waiting unless the instance/body is cancelled or an interrupting timer/conditional boundary fires. This is the core's documented first-release limit; recommend an applicable error boundary or interrupting timeout/condition for batches that must finish.

Instance/scope cancellation, termination, parent-call cancellation, or a winning error or interrupting timer/conditional boundary invalidates item publication immediately. A cancelled request may still finish externally. Its late result must fail the refreshed item/body fence without local/shared writes, aggregate writes, audit continuation, or new admission. Already committed shared writes and remote effects are not compensated. Non-interrupting boundary continuations do not settle the body or release item slots; the core owns their firing limits, activation lifetime, cancellation races, and routing.

Use the core's body activation timeline: conditional subscriptions start at token entry, including while entry waits for another body at that node; timers start once at successful body initialization before aggregate reset/admission. Both remain active through incidents and completion and close before `asyncAfter`. A caught error first closes those subscriptions, then follows partial/error publication. An interrupting timer/condition cancels unfinished work and routes once without publishing a new partial aggregate; already accepted writes, including an aggregate that triggered the condition, remain and suppress normal continuation. Non-interrupting firings create sibling tokens, with already latched occurrences/spawned siblings surviving body completion under occurrence fences. Add no per-item subscriptions.

REST transport is **at least once**. A request can succeed remotely before its response is durably recorded, or an old request can outlive a lost lease. A replacement attempt may therefore reach the receiver again. Recommend authored receiver-supported idempotency headers based on stable `sys.jobId`, as in the example; do not vary that key by retry attempt. A new body visit creates new item/job identities. The core's strict logical admission bound does not promise a physical maximum of remote requests still running after lease loss.

Shared item output conflicts and instance/shared aggregate conflicts are publication incidents over saved candidates/results. Authorized retry refreshes only the affected output fences and retries publication without HTTP invocation. It does not recalculate or merge a stale read-modify-write value; accepting that retry can overwrite a newer shared value with the saved candidate, and Operations must explain this. Parallel items writing one shared key can conflict; neither completion order nor index order grants automatic overwrite authority. Keep the candidate independent of generic job result cleanup and add an MI-specific branch in `WorkflowJobRepository` recovery, because ordinary `output_version_conflict` retry discards the snapshot/result and invokes again. Deterministic aggregate failures take the boundary or fault the instance as the core specifies. A full queue uses reserved dispatch/completion, never remote reinvocation. Shared output commits, aggregate/error publication, boundary routing, and synchronous continuation must all pass the core's monotonic shared lock-order proof.

### 5. Validation, editor, and operational UI

Add REST-specific rules to the shared automatic-MI validation owner: required async-before, one ordinary outgoing flow, local-or-writable-shared output/status targets, shared input/binding/validation dependencies, canonical-key collisions, and permitted body error/timer/conditional boundaries. Preserve instance-only conditional predicates and the existing timer-expression rules. Existing malformed imported settings must remain visible until explicitly edited/removed. Do not normalize unsupported MI input into an ordinary synchronous task.

In the canonical editor, expose the core MI section on service tasks. Enabling it enables async-before and reviews existing output mappings/status targets; permit declared locals and writable shared aliases without silently redirecting ordinary instance writes. Disabling MI or converting the node type must surface/confirm incompatible settings and must not silently promote locals to process producers. Offer body timer/conditional attachments and their interrupting setting through the core controls. Integrate accepted changes with existing undo/history handling and `changeNodeTypeFromInspector`.

Show local declarations and clearly distinguish local, shared, and unavailable ordinary-instance targets. Help must explain collection/instance snapshots, shared/settings/clock capture per item, shared publication conflicts and retry acceptance, exact-name templates, optional once-per-body async-after, Worker dependency, error and interrupting/non-interrupting boundary behavior, partial results on a caught error, and idempotency. Render sequential/parallel MI markers on the service shape; preserve accessible labels, field help, imported diagnostics, hosted dirty/saved state, and AI snapshot/apply behavior. No new editor dependency is required.

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
2. **Implement REST validation and staging.** Accept the shared MI schema only through the core path; add local/shared mapping/status validation, shared snapshots/fences, and frozen-input request construction. Gate on API/editor parity and ordinary REST regression tests.
3. **Integrate durable invocation and local finalization.** Attach the existing invoker/result-ready pipeline to item ownership. Gate on both modes, out-of-order results, no extra entry request, and empty-input behavior.
4. **Integrate failure and recovery.** Exercise retry precedence, status matching, incident recovery, lost leases, late responses, queue pressure, and aggregate conflict recovery. Prove successful request counts do not increase during unrelated recovery.
5. **Complete authoring and operations UX.** Add controls/help/markers and REST-specific item diagnostics through the core screens. Exercise both standalone and hosted editors and Worker-enabled runtime behavior in real browsers.
6. **Deliver documentation/examples and evidence.** Update affected owning guides, run the shared release gates with the other adapters, and hand results to TASK-05. Do not add a separate schema or rollout path for REST alone.

### 7. Automated and real-browser verification

Add focused REST MI cases under `Flowbit/tests/Flowbit.Tests`; proposed scenario groups are not claims that new tests already exist.

| Group | Required evidence |
| --- | --- |
| Definition/editor parity | Required async-before; both modes; local/shared output/status types/access and alias/key collisions; shared collection/URL/header/body/default/validation dependencies accepted with valid readable bindings; invalid/archived bindings and read-only output targets rejected; body timer/conditional attachment and interrupting flags; wrong/missing/null settings; imported invalid values survive redraw and round-trip. Ordinary REST/human MI behavior stays unchanged. |
| Templates and mappings | Scalar and JSON whole-item bodies, exact item/index/total names, scalar URL/header rules, escaping and JSON validation, no implicit item paths, shared collection copied once, frozen instance inputs, shared/settings/clock read at item staging and retained across retries, sequential observation of earlier shared commits, mapping defaults/required/null/array/type rules, final local completeness, empty locals, and atomic local/shared/status results. |
| Durable success and admission | Parallel responses complete in reverse order but aggregate by index; sequential sends no next request before prior item success commits; empty array sends none; duplicate inputs remain separate items; multiple Workers and queue pressure respect core admission. |
| Failure/recovery | Exact/range/catch-all/no-match; timeout/transport/invalid response/mapping/status failures; boundary-first versus retry-first; partial result published on a caught failure; exhausted incidents and same-item retry; several incidents; successful siblings stay untouched. |
| Crashes and side effects | Gates around stage, remote effect, response persistence, result-ready, local commit, and aggregate commit; restart and lease loss preserve stable idempotency headers and single local completion. Prove saved-response finalization and committed-success recovery make no extra HTTP request. |
| Cancellation and conflicts | Parallel items and other instances updating the same shared key; value changes conflict while description-only changes do not; shared item/aggregate conflict retry preserves saved candidates and HTTP counts; invalid shared batch publishes nothing; body/instance/scoped/parent-call cancellation and interrupting error/timer/conditional boundaries discard late responses; committed shared writes remain. Exercise conditions while entry waits for another body, timer activation before reset, incident/completion lifetime and closure before async-after; error capture closes subscriptions before partial/error writes; interrupting timer/condition publishes no new partial aggregate and preserves triggering accepted writes; non-interrupting repeats/latches/spawned siblings survive as specified by core occurrence fences; no shared-write conditional wake. |
| Limits/security | Core item/snapshot/result limits include shared contexts/candidates plus HTTP response bounds; no truncation; shared-binding guard before substitution; canonical multi-key lock order including boundary/continuation paths; no secrets in DTOs/logs; denied job inspection/retry; automatic items absent from human inbox/actions. |

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

Update current-behavior guides when implementation lands, coordinating common sections with TASK-05A. This planning change updates only the proposed contract.

| Owning document | REST-specific implementation update |
| --- | --- |
| [BPMN support](../bpmn-support.md), [node/property reference](../node-reference.md) | Supported REST MI subset, required async-before/optional body async-after, exact templates, local/shared output/status rules, body error and interrupting/non-interrupting timer/conditional boundaries, and deferred features. |
| [Developer guide](../developer-guide.md) | HTTP-only walkthroughs, collection/instance snapshots and per-item shared/settings context, ordered instance/shared aggregate, receiver-supported idempotency, retry/boundary differences, partial results, the stuck-body limit, cancellation, and publication-only conflict recovery with saved-value overwrite semantics. |
| [API reference](../api-guide.md), [Flowbit.Ui guide](../ui-guide.md) | REST item/status/job correlation through the core contract; authoring controls, permissions, paged results, incident recovery, and changed screenshots. |
| [Deployment/operations](../deployment.md), [runtime reference](../../Flowbit/README.md), [AGENTS.md](../../AGENTS.md) | Shared staging/ValueRevision fences, unlocked invocation, atomic local/shared settlement, monotonic lock-order proof, publication-only recovery, retained candidates, boundary lifecycle, limits, Worker/capacity interaction, and redaction. Shared migrations/settings/rollout remain owned by TASK-05A. |
| [Example catalog](../../examples/README.md), [MI examples](../../examples/multi-instance/README.md) | Controlled REST examples in both modes, object-item body, local/shared reads/writes and aggregate, side-effect-only outputs, empty collection, error/timer/conditional boundaries in both interrupting modes where applicable, and conflict recovery. State inputs, expected outputs/call counts, Worker/endpoint prerequisites, and idempotency support. |

Review [database schema](../database-schema.md) only if adapter integration changes core persistence, and return those changes to TASK-05A rather than documenting an independent model. Update root capabilities, setup instructions, or documentation links only when affected. Validate relative links/anchors/images and parse JSON fragments; exercise changed example walkthroughs over HTTP with equivalent PowerShell/Bash commands. Do not change AI tool names, descriptions, or instructions.

Before marking TASK-05B Done, attach actual automated and browser evidence here, confirm ordinary REST and human MI compatibility, and return the final adapter contract, example inputs/results, error/retry matrix, and any shared-core changes to TASK-05. Follow the parent's coordinated migration/publication/rollback plan. REST implementation is complete only when its required core integration and recovery checks pass; the parent remains open until script and call subtasks and the joint first-release gates are also complete.
