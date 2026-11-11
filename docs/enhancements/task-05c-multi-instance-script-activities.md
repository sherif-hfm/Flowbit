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

Run both NCalc and JavaScript `scriptTask` nodes once per collection item, with independent local outputs, durable recovery, and ordered aggregate results. Both script formats support parallel and sequential modes. This subtask is required for the same first release as service and call activities; splitting the plan does not defer any activity type.

This is an implementation plan based on the repository reviewed on **2026-11-11**. Automatic multi-instance scripts are not implemented. JSON examples below describe the proposed contract and are not currently runnable definitions.

[TASK-05A](task-05a-multi-instance-core.md) is authoritative for the common workflow JSON, local-output declarations, input snapshots, limits, admission, persistence, item lifecycle, aggregate publication, query/API contracts, compatibility, migration, and rollout. This page specifies the script adapter and its verification. It must not introduce another scheduler, item table, result envelope, or aggregate writer.

## First release acceptance criteria

- [ ] NCalc and JavaScript scripts each process a declared instance collection in parallel and sequential modes through the shared core, with one durable activity job per admitted item.
- [ ] Scripts read frozen instance inputs, the `config.*`/`setting.*` values and clock read when the item first runs, and read-only `mi.item`, zero-based `mi.index`, and `mi.total`. Sequential mode does not make earlier items' outputs visible to later items.
- [ ] NCalc assignment targets and JavaScript `execution.setVariable` resolve only declared `multiInstance.outputVariables`. Ordinary scripts retain their existing process-variable behavior.
- [ ] Local defaults, type conversion, nullability, and final validation are consistent across both formats. A failed attempt publishes no partial locals or process writes.
- [ ] JavaScript executes in the existing Jint sandbox outside runtime row locks, with all existing resource and value-bridge limits. Computed names cannot bypass local-only writes or shared-read restrictions.
- [ ] Retries reuse item identity and frozen inputs, start with fresh locals, and cannot reevaluate already committed successful siblings or publish through stale fences.
- [ ] Script errors and local validation failures use the existing durable retry/error-boundary policy, integrated with whole-body cancellation and incidents from TASK-05A. A caught failure publishes the core's partial result before the boundary continues.
- [ ] Shared editor authoring, invalid-import preservation, script-format/type changes, undo/redo, runtime progress/results, and permissions work in a real browser.
- [ ] Automated regressions, canonical HTTP examples, owning documentation, and browser evidence are complete before this subtask is Done. Parent TASK-05 still requires all four subtasks.

## Non-goals

No new script language, expression path syntax, JavaScript execution API, CLR/file/network access, or external script process. Do not add process/shared writes from an item, live shared reads, reductions over previous items, cardinality/quorum completion, per-item boundary events, timer/conditional boundaries on the body, or direct item cancellation. Common exclusions and later scope remain in [TASK-05A](task-05a-multi-instance-core.md).

No standalone script-item API or bespoke Operations page is needed; integrate the core's item queries, node activity, and existing job/incident recovery. Service and call behavior belongs to their adapter subtasks, and calls remain in the first release.

## Current state

- `WorkflowModel.cs` already models `scriptFormat`, ordered NCalc `assignments`, and a JavaScript `script` body. The existing multi-instance model and runtime currently describe human tasks.
- `WorkflowModelMigrator` and editor `applyTypeInvariants` remove MI settings from non-user tasks. Core normalization/validation work must preserve script MI and malformed imported settings instead of silently removing them.
- `WorkflowEngineService.Jobs.cs` contains `EvaluateStagedScript`, which evaluates against a durable snapshot and resolves writes using process-variable declarations. That target resolution cannot be reused unchanged for item locals.
- `WorkflowEngineService.cs` contains `EngineScriptContext`, script coercion/validation helpers, and ordinary script execution. `IScriptContext` and `IScriptEvaluator` in `Flowbit.Service/Abstractions/Services.cs` separate the engine's variable behavior from Jint.
- `Flowbit.Infrastructure/Scripting/JintScriptEvaluator.cs` already enforces a sandbox and bounded execution/value conversion. Its `execution` object includes variable getters/setters and `getFlowInfo`; MI does not need another scripting API.
- `SharedVariableAccessPlanner` currently treats script assignments as possible process producers, and JavaScript's computed targets can widen its writable shared-variable plan. Automatic MI must use different producer/read analysis without changing ordinary scripts.

## Plan

### 1. Integrate through the core activity-adapter seam

Implement an automatic-MI script adapter using the core's item job dispatch and fences. The adapter receives the immutable definition/script, parent snapshot, item value/index/total, stable item/job identity, current attempt metadata, and local-output declarations. It returns a complete validated local result or a classified script failure; it never writes runtime variables or advances a graph token itself.

The responsibilities are:

| Stage | Script-specific responsibility | Core responsibility |
| --- | --- | --- |
| Definition validation | Validate format/body/assignments and their local targets, context references, and local validation dependencies. | Validate common MI schema, source/result contracts, topology, limits, and durable readiness. |
| Stage item | Build an independent evaluation context from the persisted snapshot and fresh local defaults. | Own the item/job lease, persisted staging, activation/state fences, and lifecycle. |
| Evaluate | Run NCalc assignments or the existing Jint evaluator, then validate the full local output overlay. | Release runtime row locks before evaluation; apply cancellation/lease protocol. |
| Finalize | Return local results or bounded failure metadata; do not persist process writes. | Recheck fresh database state, commit successful locals once, settle failures, refill admission, and queue aggregate completion. |
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

With one item `{ "sku": "A", "quantity": 2, "unitPrice": 5 }` and `multiplier = 3`, its local result is `lineAmount = 30` and `lineSummary = { "index": 0, "sku": "A", "amount": 30 }`. The core adds the item/index/status envelope and publishes the final ordered collection. `execution.setVariable` remains the API, but its target resolution in this adapter is explicitly local.

### 4. Frozen context, output validation, and isolation

Freeze the allowed instance variables and causal actor/allowlisted claims at the core snapshot instant: successful durable body initialization, after the parent `asyncBefore` wait. Read `config.*`/`setting.*` values and `sys.now`/`sys.today` when the item first stages and keep them for its retries. Later process edits and sibling completion cannot change an item's inputs, and an incident retry reuses the item's staged values. A script that needs one setting value for the whole batch copies it into a process variable before the body.

- Supply read-only `mi.item`, `mi.index`, and `mi.total`. Do not expose `mi.completed`/`mi.remaining` to item calculations; completion-order-dependent progress is not an input contract.
- Preserve stable `sys.jobId` and existing attempt-specific `sys.jobAttempt`. Use the item's first-stage time for `sys.now`/`sys.today` and keep it across retries; never refresh frozen business inputs on retry. Do not fabricate actor identity or take an operator retry's claims as the original causal claims.
- Capture `FlowInfo` once for the body where scripts already support it. NCalc `FlowInfo` and JavaScript `execution.getFlowInfo` read this frozen evidence; reject unknown flow IDs using existing rules. Child evaluation adds no per-item parent flow traversals or human actions. `CountFlow`/`PercentFlow` outcome voting is not added.
- Initialize a new local buffer for each item attempt. Apply declared defaults using the common local contract. Unset non-nullable outputs may exist during evaluation, but every declaration must satisfy its final contract before success. Nullable unset outputs become JSON null; an empty declaration list yields `{}`.
- After each setter/assignment, preserve existing script type/coercion behavior so later statements read the staged local value. Evaluate final local validation against the completed overlay. Any invalid target, type, nullability, validation, execution error, or limit failure discards the entire staged result.
- Resolve writes only against declared locals. Reject process variables, shared aliases, reserved/context names, and undeclared targets. Do not copy local results into the instance-variable writer or include them in process-producer inference.
- Apply the core collision set after excluding locals from every automatic-MI node. Local names cannot shadow declared/discovered process inputs or producers; a local must not collide with itself through old inference. Preserve ordinary service/status/start/flow producer detection.
- Reads see frozen allowed inputs plus the current item's staged locals. Getters and the value bridge return isolated JSON values; modifying a returned object/array cannot mutate the persisted snapshot, another item, or the local buffer without an explicit setter.
- Exclude shared aliases from the item context. Reject statically identifiable shared references in assignments, defaults, and validations, including aggregate/error-target validation dependencies. Computed JavaScript reads/writes must be checked at runtime and fail with a bounded, value-free error. `getVariables` cannot expose excluded shared values. Unrelated workflow nodes may continue using shared variables normally.

Do not make a global change to `EngineScriptContext` that redirects all scripts to locals. Introduce a dedicated MI context or an explicit execution-scope adapter, while reusing established coercion and validation helpers. Tests must demonstrate that ordinary process-writing scripts and their shared-variable behavior remain unchanged.

### 5. Sandbox, jobs, failures, and cancellation

Reuse `IScriptEvaluator` and the current Jint sandbox. Preserve timeout, statement, memory, recursion, execution-stack, regex, array, value-depth/item/byte, and cancellation limits; do not relax them because evaluation now runs per item. NCalc keeps its existing evaluator and validation behavior. Apply the core snapshot/aggregate bounds in addition to evaluator limits so many individually valid items cannot create an unbounded aggregate.

Stage under the core's normal instance/gateway/token/body/item/job lock order, then commit and discard tracked state before evaluating. A JavaScript evaluator instance or mutable local overlay must not be shared between concurrent items. Finalize only after reacquiring current database state and checking item, activation, job, lease/generation, and parent eligibility. A late or cancelled evaluation cannot write locals, process values, history, or continuation.

Script parsing/execution, sandbox constraint, and local-output validation failures use the script's existing durable job policy. `boundaryFirst` tries the applicable catch-all boundary before retries; `retryFirst` exhausts the configured attempts first. Script bodies retain one catch-all error boundary with no HTTP-status matcher. No timer/conditional or item-level boundary is added.

Delegate whole-body routing/cancellation to TASK-05A. A caught failure settles the failing parent-item ledger visit, cancels unfinished sibling work, publishes the core's partial result, and routes the body once. With no matching boundary and exhausted retries, the item remains incident-blocked and the body waits; a script that throws for one item on every attempt therefore waits until the instance is cancelled, the core's documented first-release limit. Keep its ledger visit active while recovery is possible; attempts/incidents carry failure evidence. Retry the same item job with fresh locals, preserving committed siblings. Item incidents pause new admissions according to the core; resolving only one of several incidents must not unblock the whole body.

Aggregate version conflicts belong to the core completion job, whose retry must never reevaluate successful scripts; deterministic aggregate failures take the boundary or fault the instance as the core specifies. Scope cancellation, termination, fault, or parent-call cancellation immediately closes item recovery authority; all late evaluations are fenced out. Common notification draining after aggregate reset/publication and version-switch/reactivation restrictions remain core responsibilities.

### 6. Validation, editor, and runtime UI

Add script-specific rules beside the shared automatic-MI rule helper. Check supported `scriptFormat`, required body/assignment shape, local target resolution, expression dependencies, and incompatible settings before normalization loses evidence. Require the explicit `completionEvaluation: "afterAll"` the core specifies; human MI retains its existing defaults.

Update producer and shared-access analysis so automatic NCalc assignments and computed JavaScript setters cannot become process/shared producers. A downstream call must not discover a script item's local as a parent variable. Retain ordinary scripts' current access plan. Publication/readiness and AI authoring validation must call the same server rules; do not change AI instructions or tool names/descriptions.

In the canonical editor assets:

- Enable the common MI controls for both script formats and show the existing script marker together with the parallel/sequential marker. Explain local versus process targets and required durable execution through inspector help.
- Make NCalc assignment target selection use local declarations when MI is enabled. Provide examples of read-only item/index context and JavaScript local setters without offering unsupported object-path syntax.
- Enabling MI must review existing assignment targets/JavaScript writes; never silently redirect existing process writes. Disabling MI or changing type/format must surface incompatible settings rather than promote local writes to process writes. Preserve invalid imported values until explicitly edited/removed.
- Integrate accepted changes with editor-root history and `changeNodeTypeFromInspector`; preserve hosted dirty/saved state, AI snapshot/apply behavior, copy/paste, undo/redo, and dependency-free standalone loading.
- Keep accessible labels/live diagnostics and ordered save-validation arrays. Selecting/redrawing cannot rewrite MI configuration or erase script errors.

Use the core's paged automatic-item progress/results, node-activity detail, and authorized job/incident links. Label script kind/format where helpful; display local outputs rather than human User/Action columns. Do not expose claim/assignment/role actions, fetch every result on initial load, or add script text/snapshot payloads to general list responses. Protected payloads and Operations remain subject to their existing read/retry permissions.

### 7. Implementation sequence and file map

Paths below are relative to the repository root. Coordinate shared-file edits with TASK-05A; proposed adapter filenames are implementation choices, not a second public contract.

| Step | Main files/owners | Review gate |
| --- | --- | --- |
| 1. Characterize existing scripts | `Flowbit/tests/Flowbit.Tests/TypedInputsScriptTests.cs`, `JintScriptEvaluatorTests.cs`, `JintScriptEvaluatorFlowInfoTests.cs`; shared model/validator tests. | Ordinary script reads/writes, coercion, validation, sandbox, and FlowInfo behavior are pinned. |
| 2. Local evaluator adapter | `Flowbit/src/Flowbit.Service/Services/WorkflowEngineService.Jobs.cs` (`EvaluateStagedScript`), `WorkflowEngineService.cs` (`EngineScriptContext`/coercion), `Abstractions/Services.cs`; `Flowbit.Infrastructure/Scripting/JintScriptEvaluator.cs` only where isolation/cancellation integration requires it. | Both formats produce validated isolated locals through the core seam without process writes. |
| 3. Definition/access analysis | `WorkflowDefinitionValidator.cs`, `SharedVariableAccessPlanner.cs`, `SharedVariableTransactionLockOrderValidator.cs`, `CallActivityRules.SourceDeclarations`, and shared/core normalization. | Static/dynamic illegal targets and shared reads fail; ordinary scripts remain compatible; locals are never process producers. |
| 4. Durable integration | Core automatic-MI coordinator and `WorkflowEngineService.Jobs.cs`, Worker dispatch, common error/cancellation paths. | Sequential/parallel jobs, failure policies, restart/stale-result handling, and no successful sibling reevaluation pass. |
| 5. Authoring and UI | `Flowbit/src/Flowbit.Ui/wwwroot/editor/flowbit-editor.js`; template/CSS only if needed; core instance detail/Operations projections. | Round trips, invalid import, format/type changes, local controls, permissions, and real-browser scenarios pass. |
| 6. Examples/docs/handoff | Owning pages below and canonical MI examples. | HTTP scenarios, linked documentation, recorded checks, and parent release checklist agree with implemented behavior. |

The script evaluator/context work can progress after core contracts are fixed, but runtime delivery depends on the core's persistence, admission, cancellation, retention, and recovery guarantees. Do not ship a temporary all-items-in-one-transaction or in-memory fan-out implementation.

### 8. Automated and real-browser verification

Add focused automatic-MI script tests; these are required scenarios, not claims that new tests already exist.

| Group | Required evidence |
| --- | --- |
| Contract/parity | Both formats, both modes; required `asyncBefore`; `asyncAfter` once; imported invalid values; omitted, `afterEach`, and `afterAll` completion evaluation; API/editor target/declaration errors agree. |
| Inputs/locals | Every applicable declared item type; duplicates/order; empty and invalid source cases through the core; defaults, unset/null outputs, arrays/JSON, ordered NCalc writes, JavaScript read-after-write, and atomic failure. |
| Isolation | Frozen process inputs and FlowInfo despite edits/retries; settings and time read at item staging and kept across retries; returned-object mutation cannot affect snapshots/siblings; sequential items cannot read earlier outputs; unknown/local/process/shared/context target cases, including computed names and bulk getters. |
| Limits | Jint parsing/time/statements/memory/recursion/regex/array/value bridge and cancellation constraints; common snapshot/result limits; no serialization bypass or mutable evaluator sharing. |
| Durable behavior | Out-of-order parallel finishes yield ordered results; sequential retry holds its slot; stale leases/restarts/duplicate finalization; failed item versus aggregate retry; instrumented successful-evaluation counts do not increase on recovery. |
| Boundaries/cancellation | `boundaryFirst`/`retryFirst`, no boundary, script/local-validation failures, multiple incidents, whole-body interruption with its partial result, cancelled evaluator completion, and no aggregate write on cancellation or while an incident is open. |
| Regression | Ordinary scripts still write process/shared variables, human MI behavior unchanged, local producers absent from call compatibility, automatic-loop guard preserved, and protected UI payload/retry access unchanged. |

Extend relevant existing `TypedInputsScriptTests`, `JintScriptEvaluatorTests`, `JintScriptEvaluatorFlowInfoTests`, shared-variable access-plan/runtime suites, `DurableWorkflowProcessorTests`, `DurableJobConflictTests`, `DurableParallelFinalizationTests`, automatic-loop guard integration suites, `EditorValidatorTests`, and `EditorValidatorCharacterizationTests`. Core owns the full lifecycle/migration/query race matrix; include script cases rather than duplicating another runtime harness.

Run focused filters during implementation, then the full test/solution and browser gates recorded in [TASK-05A](task-05a-multi-instance-core.md). Keep PostgreSQL/Docker requirements explicit and report unavailable checks. Documentation-only planning does not claim those runtime checks passed.

Follow the [Chromium smoke README](../../Flowbit/tests/Flowbit.BrowserTests/README.md) and [Worker acceptance README](../../Flowbit/tests/Flowbit.BrowserAcceptanceTests/README.md). Keep the ordinary smoke fixture Worker-free; durable script cases use the real published API/UI, Worker, disposable PostgreSQL, and authenticated HTTP setup. Test gates cannot inject runtime state or edit database rows to manufacture results.

Exercise real input at **1440×900, 1024×768, and 390×844**:

1. Create/import both script examples in standalone and hosted editors; configure locals, change mode, edit NCalc/JavaScript, save/reopen, inspect invalid targets, switch formats/types, and undo/redo with keyboard and relevant pointer interactions.
2. Start a body with the Worker stopped, inspect queued state, start the Worker, and inspect sequential/parallel progress and index-ordered local results. Verify empty input.
3. Trigger a genuine script/limit/output failure and a boundary variant. Inspect an incident and retry through authorized Operations; confirm successful siblings are not reevaluated and unchanged invalid inputs fail again rather than silently refreshing.
4. Cancel while real item work is active or waiting; verify late evaluation cannot continue the body. Exercise the core version-switch blocker and preserve ordinary/human script/task screens.
5. Verify paged results, loading/error/empty states, keyboard-accessible help/fields, denied payload/retry access, and stale responses after identity changes.

Capture changed inspector/marker and queued, blocked, completed, and cancelled runtime screenshots. Record test commands/results, browser/version, localhost URLs, interactions, viewports, console warnings/errors, host-log findings, and unavailable checks. Jint or DOM-stub tests do not replace this real-browser evidence.

### 9. Documentation and handoff

Update behavior guides only with the implementation. This subtask plan does not claim that the proposed scripting contract has shipped.

| Owning page | Script-specific update |
| --- | --- |
| [Node/property reference](../node-reference.md) | Both formats, item context, local targets/default/null/validation rules, async requirements, and unsupported shared/progress/path operations. |
| [Developer guide](../developer-guide.md) | HTTP-only NCalc and JavaScript examples with inputs/results, frozen inputs and per-item settings/clock, retry/boundary behavior and partial results, and ordinary versus MI write scope. |
| [BPMN support](../bpmn-support.md) | Supported script MI subset and remaining limitations, alongside service/call support from their owners. |
| [Flowbit.Ui guide](../ui-guide.md) | Local assignment/script authoring, format/type transitions, result viewing, recovery links, and affected screenshots. |
| [Deployment/operations](../deployment.md) | Worker dependency, existing sandbox limits plus core byte limits, recovery, and the core rollout restrictions. |
| [Runtime reference](../../Flowbit/README.md) and [AGENTS.md](../../AGENTS.md) | Explicit MI script context, unlocked evaluation/fencing, local producer/access analysis, and sandbox invariants. |
| [Example catalog](../../examples/README.md) and [MI examples](../../examples/multi-instance/README.md) | Canonical NCalc and JavaScript definitions covering both modes, typed inputs, expected ordered outputs, empty input, and a bounded failure/recovery case. |

Coordinate API/schema documentation with TASK-05A; do not invent script-specific tables or routes. Validate relative links/anchors/images and JSON, then execute canonical examples through HTTP. Keep PowerShell and Bash walkthroughs aligned. Include examples in existing authoring-knowledge/catalog validation without changing AI instructions/tools.

Handoff to the parent requires the adapter seam and local-context contract implemented, both formats passing the required matrix, screenshots/browser evidence recorded, current documentation updated, and any limitations explicitly resolved or reflected in the parent acceptance criteria. Keep shared decisions in TASK-05A and synchronize this subtask's status with the parent/backlog. TASK-05 cannot be Done until core, service, script, and call subtasks all meet their first-release gates.
