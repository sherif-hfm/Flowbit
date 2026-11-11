# TASK-05C: Multi-instance script activities

[Parent TASK-05](task-05-multi-instance-automatic-activities.md) · [Shared core TASK-05A](task-05a-multi-instance-core.md) · [Enhancement backlog](README.md) · [Documentation home](../index.md)

| Field | Value |
| --- | --- |
| Status | Planned |
| Category | Automatic multi-instance activity adapter |
| Effort | M |
| Priority | Part of the TASK-05 first release |
| Parent | [TASK-05](task-05-multi-instance-automatic-activities.md) |
| Depends on | [TASK-05A](task-05a-multi-instance-core.md) |

## Summary

Run both NCalc and JavaScript `scriptTask` nodes once per collection item, with independent local outputs, staged shared reads/writes, durable recovery, and ordered aggregate results. Both formats support parallel and sequential modes, instance/shared typed collections, and body timer/conditional boundaries in both interrupting and non-interrupting modes. This subtask is required for the same first release as service and call activities; splitting the plan does not defer any activity type.

This is an implementation plan based on the repository reviewed on **2026-11-11**. Automatic multi-instance scripts are not implemented. JSON examples below describe the proposed contract and are not currently runnable definitions.

[TASK-05A](task-05a-multi-instance-core.md) is authoritative for the common workflow JSON, local-output declarations, input snapshots, limits, admission, persistence, item lifecycle, aggregate publication, query/API contracts, compatibility, migration, and rollout. This page specifies the script adapter and its verification. It must not introduce another scheduler, item table, result envelope, or aggregate writer.

## First release acceptance criteria

- [ ] NCalc and JavaScript scripts each process a declared instance or shared typed collection in parallel and sequential modes through the shared core, with one durable activity job per admitted item.
- [ ] Scripts read frozen instance inputs, declared shared aliases plus `config.*`/`setting.*` values and clock captured when the item first stages, and read-only `mi.item`, zero-based `mi.index`, and `mi.total`. Sequential items can observe earlier committed shared writes, but never another item's locals.
- [ ] NCalc assignment targets and JavaScript `execution.setVariable` resolve declared `multiInstance.outputVariables` or declared `readWrite` shared aliases. Ordinary scripts retain their existing process-variable behavior; direct per-item writes to ordinary instance variables remain excluded.
- [ ] Local/shared defaults, type conversion, nullability, and validation follow their respective contracts in both formats. The core commits a successful shared write batch, locals, and item completion atomically; a failed attempt publishes no partial batch.
- [ ] JavaScript executes in the existing Jint sandbox outside runtime/shared row locks, with all existing resource and value-bridge limits. Computed names cannot bypass declaration/access checks or canonical-key collision rules.
- [ ] Evaluation retries reuse item identity and frozen staged inputs and start with fresh buffers. Publication-only conflict retries preserve the saved candidate, refresh output fences through authorized recovery, and never reevaluate successful work or publish through stale activation/lease fences.
- [ ] Script errors and local validation failures use the existing durable retry/error-boundary policy, integrated with whole-body cancellation and incidents from TASK-05A. A caught failure publishes the core's partial result before the boundary continues.
- [ ] Interrupting timer/conditional boundaries cancel/fence the whole body; non-interrupting firings create a continuation while its items keep running. Attachments, subscriptions, races, and final publication follow TASK-05A, with existing instance-only conditional predicates.
- [ ] Shared editor authoring, invalid-import preservation, script-format/type changes, undo/redo, runtime progress/results, and permissions work in a real browser.
- [ ] Automated regressions, canonical HTTP examples, owning documentation, and browser evidence are complete before this subtask is Done. Parent TASK-05 still requires all four subtasks.

## Non-goals

No new script language, expression path syntax, JavaScript execution API, CLR/file/network access, or external script process. Do not add direct ordinary-instance writes from an item, live reads after item staging, reductions over previous items' local outputs, cardinality/quorum completion, per-item boundary events, or direct item cancellation. Shared inputs/targets and body timer/conditional boundaries are first-release work. The core owns their contracts; staged shared read-modify-write values are not automatic atomic reductions or conflict merges.

No standalone script-item API or bespoke Operations page is needed; integrate the core's item queries, node activity, and existing job/incident recovery. Service and call behavior belongs to their adapter subtasks, and calls remain in the first release.

## Current state

- `WorkflowModel.cs` already models `scriptFormat`, ordered NCalc `assignments`, and a JavaScript `script` body. The existing multi-instance model and runtime currently describe human tasks.
- `WorkflowModelMigrator` and editor `applyTypeInvariants` remove MI settings from non-user tasks. Core normalization/validation work must preserve script MI and malformed imported settings instead of silently removing them.
- `WorkflowEngineService.Jobs.cs` contains `EvaluateStagedScript`, which evaluates against a durable snapshot and resolves writes using process-variable declarations. That target resolution cannot be reused unchanged for item locals.
- `WorkflowEngineService.cs` contains `EngineScriptContext`, script coercion/validation helpers, and ordinary script execution. `IScriptContext` and `IScriptEvaluator` in `Flowbit.Service/Abstractions/Services.cs` separate the engine's variable behavior from Jint.
- `Flowbit.Infrastructure/Scripting/JintScriptEvaluator.cs` already enforces a sandbox and bounded execution/value conversion. Its `execution` object includes variable getters/setters and `getFlowInfo`; MI does not need another scripting API.
- `SharedVariableAccessPlanner` currently treats script assignments as possible process producers, and JavaScript's computed targets can widen its writable shared-variable plan. Automatic MI must exclude locals while retaining declared shared reads/targets and their validation dependencies, without changing ordinary scripts.
- Ordinary job incident retry clears result/error and, for `output_version_conflict`, its snapshot. The MI publication-retry branch must preserve the successfully evaluated candidate independently and never fall through to that fresh-evaluation path.

## Plan

### 1. Integrate through the core activity-adapter seam

Implement an automatic-MI script adapter using the core's item job dispatch and fences. The adapter receives the immutable definition/script, parent snapshot, staged shared context, item value/index/total, stable item/job identity, current attempt metadata, and local/shared target declarations. It returns a validated candidate containing local results and shared writes, or a classified script failure; the coordinator owns persistence and the adapter never writes runtime variables or advances a graph token itself.

The responsibilities are:

| Stage | Script-specific responsibility | Core responsibility |
| --- | --- | --- |
| Definition validation | Validate format/body/assignments, local-or-writable-shared targets, context references, and validation dependencies. | Validate common MI schema, binding/source/result contracts, alias/key collisions, topology, limits, durable readiness, and lock order. |
| Stage item | Build an independent context from the body snapshot, shared values captured now, and fresh local defaults. | Own the item/job lease, shared access locks and target value-version capture, persisted staging, activation/state fences, and lifecycle. |
| Evaluate | Run NCalc assignments or the existing Jint evaluator, then validate the complete local/shared candidate overlay. | Release runtime/shared row locks before evaluation; apply cancellation/lease protocol. |
| Finalize | Return local/shared candidates or bounded failure metadata; do not persist them directly. | Recheck fresh database state and shared `ValueRevision` fences, atomically commit shared writes/locals/success once, settle failures, refill admission, and queue aggregate completion. |
| Publish | No adapter-specific aggregate operation. | Assemble ordered results, validate/fence the aggregate write, drain notifications, and continue once. |

Require `asyncBefore: true` for MI scripts. The editor enables it as part of enabling MI; malformed imported/API definitions without it fail validation. The existing parent `asyncBefore` job initializes the body rather than evaluating the script once. `asyncAfter` remains optional and applies once after the whole aggregate succeeds, never after each item. Count the body once under the automatic-activation loop guard; item attempts must not multiply or reset that count.

Use the shared `multiInstanceItem` work kind for both formats. No per-item graph tokens, user tasks, separate scheduler, or synchronous fallback is introduced. A Worker is required even for small or empty script collections; core empty-input handling creates no script evaluations.

### 2. Proposed NCalc example

This node fragment uses a parent `amounts` variable declared `number[]`, a scalar numeric parent input `multiplier`, and scalar JSON `calculationResults` initialized to `[]`. Local names must not collide with any process input/producer. All omitted common fields follow TASK-05A.

```json
{
  "id": 40,
  "type": "scriptTask",
  "name": "Scale each amount",
  "asyncBefore": true,
  "scriptFormat": "ncalc",
  "multiInstance": {
    "mode": "sequential",
    "source": "collection",
    "collectionVariable": "amounts",
    "completionEvaluation": "afterAll",
    "maxConcurrency": 1,
    "outputVariables": [
      { "name": "scaledAmount", "dataType": "number", "nullable": false },
      { "name": "adjustedAmount", "dataType": "number", "nullable": false }
    ],
    "resultVariable": "calculationResults"
  },
  "assignments": [
    { "variable": "scaledAmount", "expression": "[mi.item] * multiplier" },
    { "variable": "adjustedAmount", "expression": "scaledAmount + [mi.index]" }
  ]
}
```

For `amounts = [10, 20]` and `multiplier = 2`, the core's ordered result rows contain local `variables` of `{ "scaledAmount": 20, "adjustedAmount": 20 }` and `{ "scaledAmount": 40, "adjustedAmount": 41 }`. The second assignment reads a prior write in the same item. It never reads the previous item's output. Switching to parallel mode changes admission only, not these results.

Resolve every assignment target against local declarations before evaluation. Preserve ordinary ordered assignment/coercion behavior and existing NCalc syntax/function casing. Use `[mi.item]`, `[mi.index]`, and `[mi.total]` for exact context names. This feature adds no dotted object-property expression language; use JavaScript for object item traversal.

### 3. Proposed JavaScript example

This fragment uses parent `orderLines` as `json[]`, numeric parent `multiplier`, and scalar JSON `lineResults` initialized to `[]`. Example items have numeric `quantity`/`unitPrice` and string `sku` properties. Object-shape validation remains authored business logic; ordinary JavaScript coercion and the existing output validation rules still apply. Authors who require strict input property types must check them explicitly before calculation.

```json
{
  "id": 41,
  "type": "scriptTask",
  "name": "Calculate order lines",
  "asyncBefore": true,
  "scriptFormat": "javascript",
  "multiInstance": {
    "mode": "parallel",
    "source": "collection",
    "collectionVariable": "orderLines",
    "completionEvaluation": "afterAll",
    "maxConcurrency": 4,
    "outputVariables": [
      { "name": "lineAmount", "dataType": "number", "nullable": false },
      { "name": "lineSummary", "dataType": "json", "nullable": false }
    ],
    "resultVariable": "lineResults"
  },
  "script": "const item = execution.getVariable('mi.item');\nconst index = execution.getVariable('mi.index');\nconst multiplier = execution.getVariable('multiplier');\nexecution.setVariable('lineAmount', item.quantity * item.unitPrice * multiplier);\nexecution.setVariable('lineSummary', { index: index, sku: item.sku, amount: execution.getVariable('lineAmount') });"
}
```

With one item `{ "sku": "A", "quantity": 2, "unitPrice": 5 }` and `multiplier = 3`, its local result is `lineAmount = 30` and `lineSummary = { "index": 0, "sku": "A", "amount": 30 }`. The core adds the item/index/status envelope and publishes the final ordered collection. `execution.setVariable` remains the API; this example uses locals, while a declared `readWrite` shared alias is also a valid target under the core's write/fence contract.

### 4. Frozen context, output validation, and isolation

Freeze the instance variables, copied collection, and causal actor/allowlisted claims at successful durable body initialization, after the parent `asyncBefore` wait. A shared typed collection is copied then and never re-enumerated on retry; reading its alias or any source resolving to that same shared key returns the frozen collection. Read other shared aliases, `config.*`/`setting.*` values, and `sys.now`/`sys.today` when the item first stages and keep them for that item's evaluation retries. A later item can see newer shared values, including a prior sequential item's committed shared writes, but not prior locals or later instance edits. A script that needs one shared/setting value for the whole batch copies it into an instance variable before the body. Publication recovery retains the saved candidate and does not refresh its business inputs.

- Supply read-only `mi.item`, `mi.index`, and `mi.total`. Do not expose `mi.completed`/`mi.remaining` to item calculations; completion-order-dependent progress is not an input contract.
- Preserve stable `sys.jobId` and existing attempt-specific `sys.jobAttempt`. Use the item's first-stage time for `sys.now`/`sys.today` and keep it across retries; never refresh frozen business inputs on retry. Do not fabricate actor identity or take an operator retry's claims as the original causal claims.
- Capture `FlowInfo` once for the body where scripts already support it. NCalc `FlowInfo` and JavaScript `execution.getFlowInfo` read this frozen evidence; reject unknown flow IDs using existing rules. Child evaluation adds no per-item parent flow traversals or human actions. `CountFlow`/`PercentFlow` outcome voting is not added.
- Initialize a new local buffer for each evaluation attempt; publication-only retry retains the saved candidate instead. Apply declared defaults using the common local contract. Unset non-nullable outputs may exist during evaluation, but every declaration must satisfy its final contract before success. Nullable unset outputs become JSON null; an empty declaration list yields `{}`.
- After each setter/assignment, preserve existing script type/coercion behavior so later statements can read that item's staged local/shared value. Evaluate local validation against the completed overlay; validate shared candidates against the matching catalog contracts before atomic publication. Any invalid target, type, nullability, validation, execution error, or limit failure publishes no part of the candidate batch.
- Resolve writes against declared locals or declared `readWrite` shared aliases. Reject ordinary instance variables, read-only shared aliases, reserved/context names, and undeclared targets. Enforce the core's alias/canonical-key collision exclusions for collection, aggregate, item-write, and error targets. Keep locals out of process-producer inference; include shared targets in producer/access/lock-order plans. A shared output is a catalog mutation, not an extra local result field.
- Apply the core collision set after excluding locals from every automatic-MI node. Local names cannot shadow declared/discovered process inputs or producers; a local must not collide with itself through old inference. Preserve ordinary service/status/start/flow producer detection.
- Reads see frozen body inputs, the item's saved shared/settings context, and its own staged local/shared writes. Getters and the value bridge return isolated JSON values; modifying a returned object/array cannot mutate the persisted snapshot, another item, catalog state, or a write buffer without an explicit setter.
- Include declared shared aliases read by assignments, defaults, validations, and JavaScript getters. Static NCalc references use exact access analysis; computed JavaScript reads and `getVariables` require the core's bounded conservative plan for available declared bindings, and computed setters include possible `readWrite` targets. Runtime checks still enforce the declaration/access and collision rules. Missing/archived/invalid shared bindings fail explicitly, while legitimate nullable values retain their contracts. Keep aggregate/error validation dependencies in the separate core publication access plan.

Do not make a global change to `EngineScriptContext` that redirects ordinary scripts. Introduce a dedicated MI context or explicit execution-scope adapter for local/shared candidates, reusing established coercion and validation helpers. Tests must demonstrate that ordinary process-writing scripts and their shared-variable behavior remain unchanged.

### 5. Sandbox, jobs, failures, and cancellation

Reuse `IScriptEvaluator` and the current Jint sandbox. Preserve timeout, statement, memory, recursion, execution-stack, regex, array, value-depth/item/byte, and cancellation limits; do not relax them because evaluation now runs per item. NCalc keeps its existing evaluator and validation behavior. Apply the core snapshot/aggregate bounds and combined 1 MiB item input/candidate limit, including shared values, in addition to evaluator limits. Retain candidates independently of job cleanup. Shared outputs keep catalog/audit evidence and bounded value-free item correlations; do not copy their values automatically into aggregate locals or ordinary instance history.

Stage under the core's normal instance/gateway/token/body/item/job lock order, acquire required shared keys in canonical ordinal order, save shared inputs and output `ValueRevision` fences, then commit and discard tracked state before evaluating. No evaluator or mutable overlay may be shared between concurrent items. Persist the successful candidate before publication; finalize only after reacquiring fresh runtime/shared locks and checking item, activation, job, lease/generation, parent eligibility, and actual shared output fences. A late or cancelled evaluation cannot write locals/shared values, history, or continuation. Extend the monotonic lock-order proof across item publication, aggregate/error targets, and body-boundary/normal continuation paths.

Script parsing/execution, sandbox constraint, and an authored local/shared candidate violating a valid target value contract use the script's existing durable job policy. `boundaryFirst` tries the applicable catch-all error boundary before retries; `retryFirst` exhausts the configured attempts first. Missing/archived bindings, binding access or contract drift, and `ValueRevision` conflicts are operational incidents, not business failures caught by an error boundary. Once evaluation has produced a saved candidate, publication recovery retains it and never reevaluates the script; before evaluation, the staging incident can retry only after its binding problem is resolved. Script bodies retain one catch-all error boundary with no HTTP-status matcher, and also support body timer/conditional boundaries in interrupting and non-interrupting modes. Per-item boundaries remain excluded. Conditional predicates and timer expressions keep their existing source contracts; shared catalog changes do not create conditional wakes.

Delegate whole-body routing/cancellation to TASK-05A. A caught failure settles the failing parent-item ledger visit, cancels unfinished sibling work, publishes the core's partial result, and routes the body once. With no matching error boundary and exhausted retries, the item remains incident-blocked and the body waits; a script that throws for one item on every attempt remains blocked unless the instance/body is cancelled or an interrupting timer/conditional boundary fires, the core's documented first-release limit. Keep its ledger visit active while recovery is possible; attempts/incidents carry failure evidence. An evaluation retry runs the same item job with fresh buffers and frozen inputs, preserving committed siblings; publication-only retry uses the saved candidate without reevaluation or fresh locals. Item incidents pause new admissions according to the core; resolving only one of several incidents must not unblock the whole body.

Shared item output conflicts open a publication incident over the retained successful candidate; aggregate conflicts belong to the core completion job. Authorized recovery refreshes only output fences and publishes saved values without script reevaluation. A saved read-modify-write result is not recalculated or merged: accepting publication retry can overwrite a newer value. Parallel writes to one shared key can conflict even within one body; sequential staging can observe earlier commits, but external writers can still cause a conflict. Add an MI-specific `WorkflowJobRepository` recovery branch so generic result/snapshot clearing cannot destroy the saved candidate. Deterministic aggregate failures take the boundary or fault the instance as the core specifies.

Scope cancellation, termination, fault, parent-call cancellation, or an interrupting timer/conditional boundary immediately closes item recovery authority and fences late evaluations. Previously committed shared writes remain committed; cancellation is not compensation. Non-interrupting boundaries leave item slots and the body intact while their own continuation runs. TASK-05A owns subscription activation/lifetime, repeating/latched firing, empty-input and completion races, notification draining, and version-switch/reactivation restrictions. Do not create one subscription per script item.

Conditional subscriptions begin at token entry even when it is queued behind another body at that node. Timers start once at successful body initialization before aggregate reset/admission. Both remain active through incidents/completion and close before async-after; caught errors cancel subscriptions before partial/error writes. Interrupting timer/conditional firing cancels unfinished work and routes once without a new partial aggregate, preserves already accepted writes including a triggering aggregate, and suppresses normal continuation. Non-interrupting firings create sibling tokens; already latched occurrences/spawned siblings survive body completion under the core's occurrence fences.

### 6. Validation, editor, and runtime UI

Add script-specific rules beside the shared automatic-MI rule helper. Check supported `scriptFormat`, required body/assignment shape, local/shared target resolution, binding access and alias/key collisions, expression dependencies, body-boundary settings, and incompatible values before normalization loses evidence. Require explicit `completionEvaluation: "afterAll"`; human MI retains its existing defaults.

Update producer and shared-access analysis so automatic NCalc assignments and computed JavaScript setters distinguish locals from declared shared producers. A downstream call must not discover a local as a parent variable. Include staged shared reads/targets, validation dependencies, and publication/continuation segments in the access and lock-order proof; retain ordinary scripts' current plan. Publication/readiness and AI authoring validation must call the same server rules; do not change AI instructions or tool names/descriptions.

In the canonical editor assets:

- Enable common MI controls for both formats and show the script and parallel/sequential markers. Explain local/shared targets, unavailable ordinary-instance writes, collection versus item snapshot timing, and required durable execution.
- Make NCalc target selection offer local declarations and declared writable shared aliases when MI is enabled. Explain staged JavaScript shared getters/setters, sequential visibility and conflict-publication acceptance without offering unsupported object-path syntax. Expose body timer/conditional attachments and their interrupting setting through core controls.
- Enabling MI must review existing assignment targets/JavaScript writes; never silently redirect existing process writes. Disabling MI or changing type/format must surface incompatible settings rather than promote local writes to process writes. Preserve invalid imported values until explicitly edited/removed.
- Integrate accepted changes with editor-root history and `changeNodeTypeFromInspector`; preserve hosted dirty/saved state, AI snapshot/apply behavior, copy/paste, undo/redo, and dependency-free standalone loading.
- Keep accessible labels/live diagnostics and ordered save-validation arrays. Selecting/redrawing cannot rewrite MI configuration or erase script errors.

Use the core's paged automatic-item progress/results, node-activity detail, and authorized job/incident links. Label script kind/format where helpful; display local outputs rather than human User/Action columns. Do not expose claim/assignment/role actions, fetch every result on initial load, or add script text/snapshot payloads to general list responses. Protected payloads and Operations remain subject to their existing read/retry permissions.

### 7. Implementation sequence and file map

Paths below are relative to the repository root. Coordinate shared-file edits with TASK-05A; proposed adapter filenames are implementation choices, not a second public contract.

| Step | Main files/owners | Review gate |
| --- | --- | --- |
| 1. Characterize existing scripts | `Flowbit/tests/Flowbit.Tests/TypedInputsScriptTests.cs`, `JintScriptEvaluatorTests.cs`, `JintScriptEvaluatorFlowInfoTests.cs`; shared model/validator tests. | Ordinary script reads/writes, coercion, validation, sandbox, and FlowInfo behavior are pinned. |
| 2. Item evaluator adapter | `Flowbit/src/Flowbit.Service/Services/WorkflowEngineService.Jobs.cs` (`EvaluateStagedScript`), `WorkflowEngineService.cs` (`EngineScriptContext`/coercion), `Abstractions/Services.cs`; `Flowbit.Infrastructure/Scripting/JintScriptEvaluator.cs` only where isolation/cancellation integration requires it. | Both formats return isolated local/shared candidates through the core seam without direct runtime writes. |
| 3. Definition/access analysis | `WorkflowDefinitionValidator.cs`, `SharedVariableAccessPlanner.cs`, `SharedVariableTransactionLockOrderValidator.cs`, `CallActivityRules.SourceDeclarations`, and shared/core normalization. | Static/dynamic declaration/access/key checks, shared reads/writes, and monotonic lock-order proof pass; ordinary scripts remain compatible; locals are never process producers. |
| 4. Durable integration | Core automatic-MI coordinator, `WorkflowEngineService.Jobs.cs`, `WorkflowJobRepository` recovery, Worker dispatch, and common boundary/cancellation paths. | Sequential/parallel jobs, saved-candidate publication recovery, body timer/conditional modes, restart/stale-result handling, and no successful reevaluation pass. |
| 5. Authoring and UI | `Flowbit/src/Flowbit.Ui/wwwroot/editor/flowbit-editor.js`; template/CSS only if needed; core instance detail/Operations projections. | Round trips, invalid import, format/type changes, local controls, permissions, and real-browser scenarios pass. |
| 6. Examples/docs/handoff | Owning pages below and canonical MI examples. | HTTP scenarios, linked documentation, recorded checks, and parent release checklist agree with implemented behavior. |

The script evaluator/context work can progress after core contracts are fixed, but runtime delivery depends on the core's persistence, admission, cancellation, retention, and recovery guarantees. Do not ship a temporary all-items-in-one-transaction or in-memory fan-out implementation.

### 8. Automated and real-browser verification

Add focused automatic-MI script tests; these are required scenarios, not claims that new tests already exist.

| Group | Required evidence |
| --- | --- |
| Contract/parity | Both formats/modes; required `asyncBefore`; `asyncAfter` once; instance/shared sources and result targets; writable/read-only shared aliases; canonical-key collisions; body timer/conditional modes; imported invalid values; omitted, `afterEach`, and `afterAll` completion evaluation; API/editor target/declaration errors agree. |
| Inputs/locals | Every applicable declared item type; duplicates/order; empty and invalid source cases through the core; defaults, unset/null outputs, arrays/JSON, ordered NCalc writes, JavaScript read-after-write, and atomic failure. |
| Isolation | Frozen instance inputs, shared collection, and FlowInfo despite edits/retries; shared/settings/time captured at item staging and kept across retries; sequential later items see earlier shared commits but not locals; returned-object mutation cannot affect snapshots/siblings/catalog; allowed local/shared and rejected read-only/instance/unknown/context targets, computed names, and bulk getters. |
| Limits | Jint parsing/time/statements/memory/recursion/regex/array/value bridge and cancellation constraints; common snapshot/result limits; no serialization bypass or mutable evaluator sharing. |
| Durable behavior | Out-of-order parallel finishes yield ordered local results; sequential retry holds its slot; stale leases/restarts/duplicate finalization; failed-evaluation versus saved-candidate publication retry; sibling/external shared-key collisions, value versus description revisions, all-or-nothing shared batches, retained candidate across generic job recovery/retention, and no increase in successful evaluation counts on publication recovery. |
| Boundaries/cancellation | `boundaryFirst`/`retryFirst`, no boundary, script/local/shared-validation failures, multiple incidents, error capture closing subscriptions before partial/error writes, conditions at queued entry, timers before reset/admission, incident/completion lifetime and closure before async-after, interrupting timer/conditional cancellation without a new partial aggregate, preserved triggering writes, non-interrupting repeats/latches/spawned siblings and empty-input/finalization races under core fences, late evaluator rejection, no rollback of committed shared writes, and no shared-write conditional wake. |
| Locking and publication | Canonical multi-key acquisition at staging/finalize, dynamic JavaScript conservative access, shared aggregate/error targets and validation dependencies, unsafe continuation rejected before evaluation, publication-only retry acceptance without recomputation or automatic conflict merge, and no aggregate write on cancellation or while its incident is unresolved. |
| Regression | Ordinary scripts still write process/shared variables, human MI behavior unchanged, local producers absent from call compatibility, automatic-loop guard preserved, and protected UI payload/retry access unchanged. |

Extend relevant existing `TypedInputsScriptTests`, `JintScriptEvaluatorTests`, `JintScriptEvaluatorFlowInfoTests`, shared-variable access-plan/runtime suites, `DurableWorkflowProcessorTests`, `DurableJobConflictTests`, `DurableParallelFinalizationTests`, automatic-loop guard integration suites, `EditorValidatorTests`, and `EditorValidatorCharacterizationTests`. Core owns the full lifecycle/migration/query race matrix; include script cases rather than duplicating another runtime harness.

Run focused filters during implementation, then the full test/solution and browser gates recorded in [TASK-05A](task-05a-multi-instance-core.md). Keep PostgreSQL/Docker requirements explicit and report unavailable checks. Documentation-only planning does not claim those runtime checks passed.

Follow the [Chromium smoke README](../../Flowbit/tests/Flowbit.BrowserTests/README.md) and [Worker acceptance README](../../Flowbit/tests/Flowbit.BrowserAcceptanceTests/README.md). Keep the ordinary smoke fixture Worker-free; durable script cases use the real published API/UI, Worker, disposable PostgreSQL, and authenticated HTTP setup. Test gates cannot inject runtime state or edit database rows to manufacture results.

Exercise real input at **1440×900, 1024×768, and 390×844**:

1. Create/import both script examples in standalone and hosted editors; configure instance/shared sources and local/shared targets, add body timer/conditional boundaries in both modes, change mode, edit NCalc/JavaScript, save/reopen, inspect invalid targets, switch formats/types, and undo/redo with keyboard and relevant pointer interactions.
2. Start a body with the Worker stopped, inspect queued state, start the Worker, and inspect sequential/parallel progress and index-ordered local results. Verify empty input.
3. Trigger a genuine script/limit/output failure and a boundary variant. Inspect an incident and retry through authorized Operations; confirm successful siblings are not reevaluated and unchanged invalid inputs fail again. Cause a shared publication conflict, inspect saved-value retry consequences, authorize recovery, and verify evaluation counts do not increase.
4. Cancel or fire an interrupting timer/conditional boundary while real item work is active/waiting; verify late evaluation cannot write shared values or continue the body. Fire non-interrupting boundaries and verify the body continues. Exercise the core version-switch blocker and preserve ordinary/human script/task screens.
5. Verify paged results, loading/error/empty states, keyboard-accessible help/fields, denied payload/retry access, and stale responses after identity changes.

Capture changed inspector/marker and queued, blocked, completed, and cancelled runtime screenshots. Record test commands/results, browser/version, localhost URLs, interactions, viewports, console warnings/errors, host-log findings, and unavailable checks. Jint or DOM-stub tests do not replace this real-browser evidence.

### 9. Documentation and handoff

Update behavior guides only with the implementation. This subtask plan does not claim that the proposed scripting contract has shipped.

| Owning page | Script-specific update |
| --- | --- |
| [Node/property reference](../node-reference.md) | Both formats, item context, local/shared targets and validation, instance/shared collections/aggregate, async requirements, body timer/conditional modes, and unsupported ordinary-instance/progress/path operations. |
| [Developer guide](../developer-guide.md) | HTTP-only NCalc and JavaScript examples with frozen instance/collection inputs and per-item shared/settings/clock snapshots, sequential shared visibility, publication-only conflict acceptance, retry/boundary behavior and partial results, and ordinary versus MI write scope. |
| [BPMN support](../bpmn-support.md) | Supported script MI subset and remaining limitations, alongside service/call support from their owners. |
| [Flowbit.Ui guide](../ui-guide.md) | Local assignment/script authoring, format/type transitions, result viewing, recovery links, and affected screenshots. |
| [Deployment/operations](../deployment.md) | Worker dependency, existing sandbox limits plus shared/core byte limits, retained candidates, shared value-conflict recovery, body-boundary operation, and core rollout restrictions. |
| [Runtime reference](../../Flowbit/README.md) and [AGENTS.md](../../AGENTS.md) | Explicit MI local/shared context, unlocked evaluation/value fencing, shared producer/access/lock-order proof, publication-only recovery, and sandbox invariants. |
| [Example catalog](../../examples/README.md) and [MI examples](../../examples/multi-instance/README.md) | Canonical NCalc and JavaScript definitions in both modes with typed instance/shared inputs, ordered locals and shared effects, empty input, timer/conditional boundaries in both modes, and bounded failure/conflict recovery. |

Coordinate API/schema documentation with TASK-05A; do not invent script-specific tables or routes. Validate relative links/anchors/images and JSON, then execute canonical examples through HTTP. Keep PowerShell and Bash walkthroughs aligned. Include examples in existing authoring-knowledge/catalog validation without changing AI instructions/tools.

Handoff to the parent requires the adapter seam and local-context contract implemented, both formats passing the required matrix, screenshots/browser evidence recorded, current documentation updated, and any limitations explicitly resolved or reflected in the parent acceptance criteria. Keep shared decisions in TASK-05A and synchronize this subtask's status with the parent/backlog. TASK-05 cannot be Done until core, service, script, and call subtasks all meet their first-release gates.
