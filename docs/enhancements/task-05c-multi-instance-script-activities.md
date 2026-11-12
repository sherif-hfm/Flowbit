# TASK-05C: Multi-instance script activities

[Parent TASK-05](task-05-multi-instance-automatic-activities.md) · [Shared core TASK-05A](task-05a-multi-instance-core.md) · [Enhancement backlog](README.md) · [Documentation home](../index.md)

| Field | Value |
| --- | --- |
| Status | In progress |
| Category | Automatic multi-instance activity adapter |
| Effort | M |
| Priority | Part of the TASK-05 first release |
| Parent | [TASK-05](task-05-multi-instance-automatic-activities.md) |
| Depends on | [TASK-05A](task-05a-multi-instance-core.md) |

## Summary

Run both NCalc and JavaScript `scriptTask` nodes once per collection item, with independent local outputs, staged shared reads/writes, durable recovery, and ordered aggregate results. Both formats support parallel and sequential modes, instance/shared typed collections, and body timer/conditional boundaries in both interrupting and non-interrupting modes. This subtask is required for the same first release as service and call activities; splitting the plan does not defer any activity type.

This is an implementation plan. It was first written against the repository reviewed on **2026-11-11**, before the TASK-05A foundation landed, and was revised on **2026-11-12** against that foundation (commit `03aeb8a`); see [Plan review](#plan-review-2026-11-12). The script adapter was implemented on **2026-11-12**; see [Implementation record](#implementation-record-2026-11-12). Script bodies publish and run only on Development API and Worker hosts with the release switch open; every other host still refuses them, and the JSON examples below run only there.

[TASK-05A](task-05a-multi-instance-core.md) is authoritative for the common workflow JSON, local-output declarations, input snapshots, limits, admission, persistence, item lifecycle, aggregate publication, query/API contracts, compatibility, migration, and rollout. This page specifies the script adapter and its verification. It must not introduce another scheduler, item table, result envelope, or aggregate writer.

## First release acceptance criteria

- [ ] NCalc and JavaScript scripts each process a declared instance or shared typed collection in parallel and sequential modes through the shared core, with one durable activity job per admitted item.
- [ ] Scripts read frozen instance inputs, declared shared aliases plus `config.*`/`setting.*` values and clock captured when the item first stages, and read-only `mi.item`, zero-based `mi.index`, and `mi.total`. Sequential items can observe earlier committed shared writes, but never another item's locals.
- [ ] NCalc assignment targets and JavaScript `execution.setVariable` resolve declared `multiInstance.outputVariables` or declared `readWrite` shared aliases. Ordinary scripts retain their existing process-variable behavior; direct per-item writes to ordinary instance variables remain excluded.
- [ ] Local/shared defaults, type conversion, nullability, and validation follow their respective contracts in both formats. The core commits a successful shared write batch, locals, and item completion atomically; a failed attempt publishes no partial batch.
- [ ] JavaScript executes in the existing Jint sandbox outside runtime/shared row locks, with all existing resource and value-bridge limits. Computed names cannot bypass declaration/access checks or canonical-key collision rules.
- [ ] Evaluation retries reuse item identity and frozen staged inputs and start with fresh buffers. Publication-only conflict retries preserve the saved candidate, refresh output fences through authorized recovery, and never reevaluate successful work or publish through stale activation/lease fences.
- [ ] Script errors and authored output failures use the existing durable retry/error-boundary policy, integrated with whole-body cancellation and incidents from TASK-05A. A caught failure publishes the core's partial result before the boundary continues. A result that retrying cannot change, such as one over a size limit, is refused through the core; it is never retried and never waits in an incident (§5).
- [ ] Interrupting timer/conditional boundaries cancel/fence the whole body; non-interrupting firings create a continuation while its items keep running. Attachments, subscriptions, races, and final publication follow TASK-05A, with existing instance-only conditional predicates.
- [ ] Shared editor authoring, invalid-import preservation, script-format/type changes, undo/redo, runtime progress/results, and permissions work in a real browser.
- [ ] Until the parent's integration step, script bodies publish and run only on a Development host with the release switch open. Service and call bodies stay rejected until their own adapters land, and every other environment rejects all three (§1).
- [ ] Automated regressions, HTTP scenarios, and browser evidence are complete, and the script sections of the owning guides and the canonical examples are prepared, before this subtask is Done. They merge into the guides and the example catalog in the parent's integration change, as [TASK-05A §13](task-05a-multi-instance-core.md#13-documentation-and-completion-handoff) requires (§9). Parent TASK-05 still requires all four subtasks.

## Non-goals

No new script language, expression path syntax, JavaScript execution API, CLR/file/network access, or external script process. Do not add direct ordinary-instance writes from an item, live reads after item staging, reductions over previous items' local outputs, cardinality/quorum completion, per-item boundary events, or direct item cancellation. Shared inputs/targets and body timer/conditional boundaries are first-release work. The core owns their contracts; staged shared read-modify-write values are not automatic atomic reductions or conflict merges.

No standalone script-item API or bespoke Operations page is needed; integrate the core's item queries, node activity, and existing job/incident recovery. Service and call behavior belongs to their adapter subtasks, and calls remain in the first release.

## Current state

Checked against the code on **2026-11-12**, after the TASK-05A foundation (commit `03aeb8a`).

Already provided by the foundation; this subtask uses it and does not rebuild it:

- **Model and loaders.** `WorkflowModelMigrator` and the editor keep `multiInstance` on service, script, and call nodes and preserve invalid imported values.
- **Common rules.** `AutomaticMultiInstanceRules` and `WorkflowDefinitionValidator` require `asyncBefore`, the explicit `afterAll`, and that every NCalc assignment target is a declared local output or a `readWrite` shared alias other than the collection, aggregate, and boundary error targets. The editor's `automaticMultiInstanceProblem` mirrors them. JavaScript targets are computed, so nothing checks them at save.
- **Shared access plans.** `SharedVariableAccessPlanner` builds one plan per phase (initialize, stage item, publish item, complete, error). NCalc plans are exact. A JavaScript body's staging plan reads every shared binding of the definition, and its staging and publication plans treat every `readWrite` binding except the reserved targets as a possible producer.
- **Coordinator.** `AutomaticMultiInstanceCoordinator` provides `InitializeBodyAsync`, `CaptureItemContextAsync`, `SaveCandidateAsync`, `PublishCandidateAsync`, `CaptureItemFailureAsync`, and `RefuseItemResultAsync`. `CaptureItemContextAsync` already builds the item context: staged shared values and output fences, `mi.item`/`mi.index`/`mi.total`, `sys.jobId`/`sys.jobAttempt`, the frozen `FlowInfo`, and fresh local defaults for each attempt.
- **Publication retry.** `WorkflowJobRepository.RetryIncidentAsync` keeps a saved candidate and requests a fence refresh only for `output_version_conflict`.
- **Editor section.** `renderAutomaticMultiInstanceSection` shows the common controls when the release switch is open.

Still closed or missing; this subtask owns the script part:

- **Three closed guards.** `WorkflowDefinitionReadinessChecker.EnsureDurablePublicationAllowed` refuses to publish any definition with an automatic body, whatever the release switch says. `WorkflowEngineService.EnsureAutomaticMultiInstanceRuntimeAvailable` throws at every entry path. A leased `multiInstanceItem` job throws "The automatic activity item adapter is not installed."
- **Entry and item processing.** Nothing routes a body's `asyncBefore` entry job to `InitializeBodyAsync` or executes an item job.
- **Script target resolution.** `EvaluateStagedScript` in `WorkflowEngineService.Jobs.cs` and `EngineScriptContext` in `WorkflowEngineService.cs` resolve write targets from process-variable declarations only, so they cannot be reused unchanged for item locals. `IScriptContext` and `IScriptEvaluator` in `Flowbit.Service/Abstractions/Services.cs` separate the engine's variable behavior from Jint, and `JintScriptEvaluator` already enforces the sandbox and bounded value conversion; MI needs no other scripting API.
- **Script authoring.** The NCalc target choices, JavaScript help, and format/type transitions in the editor have not been checked against the script contract below.

## Plan

### 1. Integrate through the core activity-adapter seam

Implement the script adapter on the coordinator operations the foundation already provides. The adapter names the instance inputs to freeze, supplies the permitted `config.*`/`setting.*`/`sys.*` values when an item first stages, evaluates the script in a dedicated context, and returns a candidate of local results and shared writes or a classified failure (§5). The coordinator owns persistence, and the adapter never writes runtime variables or advances a graph token itself.

The responsibilities are:

| Stage | Script-specific responsibility | Core responsibility (implemented) |
| --- | --- | --- |
| Definition validation | Add what the common rules do not check: context references in a body's expressions and the remaining script cases in §6. | Common MI schema, `asyncBefore`, `afterAll`, NCalc target resolution, alias/key collisions, topology, limits, and lock order. |
| Initialize body | Route the body's `asyncBefore` entry job to `InitializeBodyAsync` and name the instance inputs it must freeze (§4). | Capture the collection and those inputs under the initialization locks, reset the aggregate, arm timers, and queue the first dispatch or the empty completion. |
| Stage item | Pass the permitted `config.*`/`setting.*`/`sys.*` values and the stage time to `CaptureItemContextAsync`, in a transaction committed before evaluation. | Lock the phase's shared rows, persist the staged shared values and output fences once, and build the context with `mi.*`, job identity, frozen `FlowInfo`, and fresh local defaults for this attempt. |
| Evaluate | Run NCalc assignments or the existing Jint evaluator in the MI script context, then validate the complete local/shared overlay. | Hold no runtime or shared row lock during evaluation; apply the cancellation/lease protocol. |
| Finalize | Build the candidate (§4), commit it with `SaveCandidateAsync`, then call `PublishCandidateAsync`; on failure call the coordinator exit §5 assigns. | Recheck fresh state and `ValueRevision` fences, commit shared writes, locals, and success once, refill admission, and queue aggregate completion. |
| Publish | No adapter-specific aggregate operation. | Assemble ordered results, validate/fence the aggregate write, drain notifications, and continue once. |

An item job that finds a saved candidate goes straight to publication. This holds for every retry, not only authorized conflict recovery: a database failure after `SaveCandidateAsync` committed must not evaluate the script again.

**Release boundary.** The foundation's three guards (see Current state) are unconditional, so no script body can be published or run today, even on a Development host. Replace each with a check by activity kind:

- Publication is allowed when the release switch is enabled and every automatic body in the definition has an installed adapter.
- Runtime entry and item processing proceed for a kind whose adapter is installed and keep failing closed for the others.
- Tests pin that service and call bodies stay rejected while only the script adapter exists, and that hosts outside Development reject script bodies too.

TASK-05B needs the same routing in the same block of `WorkflowEngineService.Jobs.cs`. Whichever adapter lands first adds one dispatch by node type for the entry job and the item job; the other adds its case to it. The per-kind opening is a shared contract, recorded in [TASK-05A §8](task-05a-multi-instance-core.md#8-definition-validation-compatibility-and-safety-of-loading) under **Opening by activity kind**.

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

Resolve every assignment target against the local declarations and the declared `readWrite` shared aliases before evaluation. Preserve ordinary ordered assignment/coercion behavior and existing NCalc syntax/function casing. Use `[mi.item]`, `[mi.index]`, and `[mi.total]` for exact context names. This feature adds no dotted object-property expression language; use JavaScript for object item traversal.

Define and test what `[mi.item]` gives an NCalc expression for each element type: `string`, `number`, `boolean`, `date` and `datetime` (as their authored text), and `json`. A `json` element that is an object or array has no usable NCalc value, so decide whether a body with a `json[]` collection and NCalc assignments reading `mi.item` is rejected at save or fails at evaluation, and make the editor say so.

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

The coordinator's operations fix the rest of the adapter contract. Specify and test each point:

- **Instance inputs to freeze.** `InitializeBodyAsync` freezes only the instance variables the adapter names. For NCalc, name the variables that assignment expressions, local default templates, and local validations reference. JavaScript can read any name, including stored variables no declaration mentions, so either list the stored names under the instance lock or ask TASK-05A for an "every instance variable" request. An entry whose snapshot exceeds 1 MiB fails before any item runs; test a JavaScript body near that limit.
- **Staged environment.** `CaptureItemContextAsync` accepts only `config.*`, `setting.*`, and `sys.*` values and persists them on the item until the body settles. NCalc passes only the names its expressions reference. JavaScript needs all of them, so every item of up to 1,000 stores a copy that counts toward its 1 MiB limit. Measure that, test it, and document it; if it is too large, agree a body-level copy with TASK-05A instead of trimming what a script can read.
- **Validation context.** The candidate's `ValidationContext` is saved with it and counts toward the same limit. Supply only the names the local `validation` expressions reference, never the whole evaluation context.
- **Shared output fences.** Staging captures a fence for every possible producer; for JavaScript that is every `readWrite` binding. `SaveCandidateAsync` refuses a candidate whose fences are not exactly its written aliases, and one that writes the same shared key through two aliases. Narrow the fences to the aliases the script wrote, and reject the second alias as a script error at `setVariable`.
- **JavaScript lock scope.** Each JavaScript item locks every `readWrite` binding of the definition when it stages. Parallel items and other instances using those keys therefore wait on each other for those short transactions. Test it with several Workers, and tell authors to declare only the bindings a body needs, or to use NCalc, whose plan is exact.
- **Shared binding without a value.** An automatic phase fails with a conflict when a binding it reads has no current value (`Unset_shared_validation_dependency_prevents_the_error_write` pins this). A JavaScript body's staging plan reads every binding, so by that rule it cannot stage while any binding of its definition was never set, including a `readWrite` target the script is about to set for the first time, while an NCalc body can make that first write. Confirm this with a PostgreSQL test, then agree a rule with TASK-05A before implementing: proposed, a binding in the conservative JavaScript scope that has no value is absent from the context (`hasVariable` is false), as in an ordinary script, while a statically named dependency keeps failing closed.
- **Reads of a local without a value.** The context holds a declared local only once it has a default or a write. Define `getVariable`, `hasVariable`, and an NCalc reference for a local in neither state, and keep both formats consistent with an ordinary script's missing variable.
- **Bulk and reserved reads.** State what `execution.getVariables()` returns (frozen instance inputs, staged shared values, `mi.*`, context values, and the item's own writes) and what an item reads from its own `resultVariable` and `collectionVariable` names.

### 5. Sandbox, jobs, failures, and cancellation

Reuse `IScriptEvaluator` and the current Jint sandbox. Preserve timeout, statement, memory, recursion, execution-stack, regex, array, value-depth/item/byte, and cancellation limits; do not relax them because evaluation now runs per item. NCalc keeps its existing evaluator and validation behavior. Apply the core snapshot/aggregate bounds and combined 1 MiB item input/candidate limit, including shared values, in addition to evaluator limits. Retain candidates independently of job cleanup. Shared outputs keep catalog/audit evidence and bounded value-free item correlations; do not copy their values automatically into aggregate locals or ordinary instance history.

Stage under the core's normal instance/gateway/token/body/item/job lock order, acquire required shared keys in canonical ordinal order, save shared inputs and output `ValueRevision` fences, then commit and discard tracked state before evaluating. No evaluator or mutable overlay may be shared between concurrent items. Persist the successful candidate before publication; finalize only after reacquiring fresh runtime/shared locks and checking item, activation, job, lease/generation, parent eligibility, and actual shared output fences. A late or cancelled evaluation cannot write locals/shared values, history, or continuation. Extend the monotonic lock-order proof across item publication, aggregate/error targets, and body-boundary/normal continuation paths.

Script parsing/execution, sandbox constraint, and an authored local/shared candidate violating a valid target value contract use the script's existing durable job policy. `boundaryFirst` tries the applicable catch-all error boundary before retries; `retryFirst` exhausts the configured attempts first. Missing/archived bindings, binding access or contract drift, and `ValueRevision` conflicts are operational incidents, not business failures caught by an error boundary. Once evaluation has produced a saved candidate, publication recovery retains it and never reevaluates the script; before evaluation, the staging incident can retry only after its binding problem is resolved. Script bodies retain one catch-all error boundary with no HTTP-status matcher, and also support body timer/conditional boundaries in interrupting and non-interrupting modes. Per-item boundaries remain excluded. Conditional predicates and timer expressions keep their existing source contracts; shared catalog changes do not create conditional wakes.

Map every failure to one coordinator exit. The foundation offers four, and this table is the adapter's contract:

| Failure | Detected | Exit |
| --- | --- | --- |
| Missing body, script exception, sandbox limit, NCalc evaluation error, coercion to a declared type, or a write the MI context rejects (undeclared, ordinary instance, read-only shared, context, or reserved target). | Adapter, during evaluation. | Authored failure: the node's `job` policy. `boundaryFirst` takes the catch-all boundary through `CaptureItemFailureAsync`; `retryFirst` retries first; without a boundary the item's job opens an incident after its attempts. |
| A declared non-nullable local left without a value, a local `validation` that is false, or a shared write failing its catalog type, nullability, or validation. | Adapter, after evaluation, with the helpers the coordinator uses. `SaveCandidateAsync` repeats the check and answers `multi_instance_result_invalid`. | The same authored failure. The coordinator's refusal is a second line, not the classifier. |
| Staged input plus candidate over 1 MiB (`SaveCandidateAsync`), or the accepted locals taking the body past its result limit (`PublishCandidateAsync`): `multi_instance_result_too_large`. | Coordinator. | `RefuseItemResultAsync` in a fresh transaction. Never retried and never left in an incident. |
| Shared output `ValueRevision` changed since staging. | `PublishCandidateAsync`. | Publication incident over the saved candidate; authorized retry refreshes the fence without evaluating again. |
| Missing or archived binding, access or contract drift, lost lease, or a stale activation. | Coordinator. | Operational conflict or incident; never an error boundary. |

One case needs a decision with TASK-05A. `PublishCandidateAsync` validates the saved locals again against the shared values current at publication, so a local `validation` that reads a shared alias can pass at evaluation and fail at publication with no version conflict. Evaluating again is not allowed, and the candidate cannot change. Proposed: treat it as the authored output failure of the second row, applied to the saved candidate.

A script over frozen inputs gives the same result on every attempt, except for `sys.jobAttempt`, the clock, and randomness it reads itself. Retries therefore rarely help a script, and `retryFirst` only delays the boundary by the retry schedule. Say so in the field help and guides, and recommend an error boundary on a script body.

Delegate whole-body routing/cancellation to TASK-05A. A caught failure settles the failing parent-item ledger visit, cancels unfinished sibling work, publishes the core's partial result, and routes the body once. With no matching error boundary and exhausted retries, the item remains incident-blocked and the body waits; a script that throws for one item on every attempt remains blocked unless the instance/body is cancelled or an interrupting timer/conditional boundary fires, the core's documented first-release limit. Keep its ledger visit active while recovery is possible; attempts/incidents carry failure evidence. An evaluation retry runs the same item job with fresh buffers and frozen inputs, preserving committed siblings; publication-only retry uses the saved candidate without reevaluation or fresh locals. Item incidents pause new admissions according to the core; resolving only one of several incidents must not unblock the whole body.

Shared item output conflicts open a publication incident over the retained successful candidate; aggregate conflicts belong to the core completion job. Authorized recovery refreshes only output fences and publishes saved values without script reevaluation. A saved read-modify-write result is not recalculated or merged: accepting publication retry can overwrite a newer value. Parallel writes to one shared key can conflict even within one body; sequential staging can observe earlier commits, but external writers can still cause a conflict. The foundation's `RetryIncidentAsync` already keeps the saved candidate and requests a fence refresh only for `output_version_conflict`; pin it with script cases instead of adding another branch. Deterministic aggregate failures take the boundary or fault the instance as the core specifies.

Scope cancellation, termination, fault, parent-call cancellation, or an interrupting timer/conditional boundary immediately closes item recovery authority and fences late evaluations. Previously committed shared writes remain committed; cancellation is not compensation. Non-interrupting boundaries leave item slots and the body intact while their own continuation runs. TASK-05A owns subscription activation/lifetime, repeating/latched firing, empty-input and completion races, notification draining, and version-switch/reactivation restrictions. Do not create one subscription per script item.

Conditional subscriptions begin at token entry even when it is queued behind another body at that node. Timers start once at successful body initialization before aggregate reset/admission. Both remain active through incidents/completion and close before async-after; caught errors cancel subscriptions before partial/error writes. Interrupting timer/conditional firing cancels unfinished work and routes once without a new partial aggregate, preserves already accepted writes including a triggering aggregate, and suppresses normal continuation. Non-interrupting firings create sibling tokens; already latched occurrences/spawned siblings survive body completion under the core's occurrence fences.

### 6. Validation, editor, and runtime UI

The common rules already check `asyncBefore`, the explicit `afterAll`, NCalc target resolution, binding access, alias/key collisions, body-boundary settings, and the topology; the access planner and the lock-order proof already cover every automatic phase. Verify them with script cases and change them only where a case fails. Add the script rules the foundation does not have, in the server validator and in the editor's save validator with the same messages and error order:

- **Context references.** Ordinary scripts are only parse-checked, so nothing rejects `[mi.completed]`, `[mi.remaining]`, or an unknown `mi.*` name in a body's assignment, local default, or local validation today. Reject them at save for an NCalc body; validation of ordinary script tasks stays unchanged.
- **Local validation language.** A local output's `validation` is only parse-checked, so it accepts `FlowInfo` and gateway functions, which TASK-05A lists as an open finding. Take ownership here: reject `FlowInfo` and the gateway and multi-instance outcome functions, as other variable and output validation expressions do.
- **JavaScript.** Targets stay a runtime check. The save validator cannot prove a computed name, so the field help states the allowed targets and the runtime error names the rejected one.
- **Evidence.** Check these before normalization can lose the authored value.

A downstream call must not discover a local as a parent variable; pin that with a script case. Publication/readiness and AI authoring validation must call the same server rules; do not change AI instructions or tool names/descriptions.

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
| 3. Definition rules | `WorkflowDefinitionValidator.cs`, `AutomaticMultiInstanceRules.cs`, and the editor save validator for the §6 additions; script cases for the existing `SharedVariableAccessPlanner.cs`, `SharedVariableTransactionLockOrderValidator.cs`, and `CallActivityRules.SourceDeclarations` behavior. | Context-reference and local-validation rules agree between server and editor; existing access plans and lock-order proof pass for both formats; ordinary scripts remain compatible; locals are never process producers. |
| 4. Durable integration and release boundary | `WorkflowDefinitionReadinessChecker.cs`, `WorkflowEngineService.AutomaticMultiInstance.cs` (runtime guard), `WorkflowEngineService.Jobs.cs` (entry and item routing, shared with TASK-05B), the coordinator's existing operations, and common boundary/cancellation paths. `WorkflowJobRepository` recovery needs tests only. | Script bodies publish and run only on a Development host with the switch open; service and call bodies stay closed. Sequential/parallel jobs, every failure exit in §5, saved-candidate publication recovery, body timer/conditional modes, restart/stale-result handling, and no successful reevaluation pass. |
| 5. Authoring and UI | `Flowbit/src/Flowbit.Ui/wwwroot/editor/flowbit-editor.js`; template/CSS only if needed; core instance detail/Operations projections. | Round trips, invalid import, format/type changes, local controls, permissions, and real-browser scenarios pass. |
| 6. Examples/docs/handoff | Prepared script sections for the owning pages below and prepared canonical MI examples, merged in the parent's integration change (§9). | HTTP scenarios, prepared documentation, recorded checks, and parent release checklist agree with implemented behavior. |

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
| Release boundary | With only the script adapter installed: script bodies publish and run on a Development host with the switch open; service and call bodies are refused at publication, entry, and item processing; a host outside Development refuses script bodies whatever its options say. |
| Failure exits | One case per row of the §5 table, in both formats, with and without a boundary and under both `failureHandling` values; over-limit candidates and results are refused without a retry or an incident; a local validation that fails only at publication follows the agreed rule; a retry after a committed candidate never evaluates again. |
| Adapter contract | Named instance inputs for NCalc and the whole-instance snapshot for JavaScript, including a stored variable no declaration names and a snapshot near 1 MiB; staged `config.*`/`setting.*` size per item; minimal validation context; JavaScript fences narrowed to written aliases; one shared key written through two aliases; a shared binding without a value in both formats; reads of a local without a value; `getVariables()`; `mi.item` for every element type; rejected `mi.completed`/`mi.remaining` references; `usesFlowInfo` on a JavaScript body. |
| Scale | A 1,000-item body in both formats with default Worker settings and with several Workers, recording duration and queries per item. Scripts are the fastest items, so they expose the per-item cost TASK-05A lists as an open finding (each completion reads progress for every body of the instance and lists its open jobs) and the JavaScript lock scope of §4. |

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

This subtask plan does not claim that the proposed scripting contract has shipped. Follow the timing in [TASK-05A §13](task-05a-multi-instance-core.md#13-documentation-and-completion-handoff):

- **With this subtask's code:** implementation invariants in [AGENTS.md](../../AGENTS.md), and any operational, setting, schema, or route reference this subtask changes, marked inactive until automatic multi-instance is enabled.
- **Prepared here, merged in the parent's integration change:** the script sections of the behavior guides in the table below and the canonical examples. The AI authoring package bundles those guides and the example catalog, so merging them earlier would tell the assistant to author definitions that publication still refuses. Until then keep the example definitions as test fixtures outside `examples/`, run them over HTTP on the Development test hosts, and record the contract in this plan.

| Owning page | Script-specific update |
| --- | --- |
| [Node/property reference](../node-reference.md) | Both formats, item context, local/shared targets and validation, instance/shared collections/aggregate, async requirements, body timer/conditional modes, and unsupported ordinary-instance/progress/path operations. |
| [Developer guide](../developer-guide.md) | HTTP-only NCalc and JavaScript examples with frozen instance/collection inputs and per-item shared/settings/clock snapshots, sequential shared visibility, publication-only conflict acceptance, retry/boundary behavior and partial results, and ordinary versus MI write scope. |
| [BPMN support](../bpmn-support.md) | Supported script MI subset and remaining limitations, alongside service/call support from their owners. |
| [Flowbit.Ui guide](../ui-guide.md) | Local assignment/script authoring, format/type transitions, result viewing, recovery links, and affected screenshots. |
| [Deployment/operations](../deployment.md) | Worker dependency, existing sandbox limits plus shared/core byte limits, retained candidates, shared value-conflict recovery, body-boundary operation, and core rollout restrictions. |
| [Runtime reference](../../Flowbit/README.md) and [AGENTS.md](../../AGENTS.md) | Explicit MI local/shared context, unlocked evaluation/value fencing, shared producer/access/lock-order proof, publication-only recovery, and sandbox invariants. |
| [Example catalog](../../examples/README.md) and [MI examples](../../examples/multi-instance/README.md) | Canonical NCalc and JavaScript definitions in both modes with typed instance/shared inputs, ordered locals and shared effects, empty input, timer/conditional boundaries in both modes, and bounded failure/conflict recovery. |

Coordinate API/schema documentation with TASK-05A; do not invent script-specific tables or routes. Validate relative links/anchors/images and JSON, then execute the prepared examples through HTTP. Keep PowerShell and Bash walkthroughs aligned. The integration change adds the examples to the existing authoring-knowledge/catalog validation without changing AI instructions/tools.

Handoff to the parent requires the adapter seam and local-context contract implemented, both formats passing the required matrix, screenshots/browser evidence recorded, the documentation above updated or prepared as its timing requires, and any limitations explicitly resolved or reflected in the parent acceptance criteria. Keep shared decisions in TASK-05A and synchronize this subtask's status with the parent/backlog. TASK-05 cannot be Done until core, service, script, and call subtasks all meet their first-release gates.

## Plan review (2026-11-12)

This plan was reviewed against the parent plan, TASK-05A, and the foundation code (commit `03aeb8a`). No script-adapter code exists yet. The review changed the plan as follows:

- **Current state** now separates what the foundation provides from what this subtask must add. Target resolution for NCalc, the per-phase shared access plans, the item context, and publication-only retry are already implemented; the earlier text planned them again.
- **Release boundary** (§1) is new. The foundation's publication, runtime, and item-job guards are unconditional, and no adapter plan said how a subtask opens them for its own acceptance evidence.
- **Adapter contract** (§4) records what the coordinator's operations require: named instance inputs, the staged environment, a minimal validation context, narrowed JavaScript fences, the JavaScript lock scope, and read semantics.
- **Failure exits** (§5) map every failure to one coordinator operation. The earlier text sent size-limit failures through the retry policy, which the core's refusal rule forbids.
- **Validation** (§6) names the two rules still missing: context references and the local-validation language.
- **Tests** (§8) add the release boundary, failure exits, adapter contract, and scale rows.
- **Documentation timing** (§9 and the acceptance criteria) now follows TASK-05A §13. The earlier criteria required guides and canonical examples before integration, when publication still refuses them.

The per-kind opening of the publication, runtime, and item-job guards (§1) was decided on 2026-11-12 and is recorded in TASK-05A §8, TASK-05B, and TASK-05D.

Decisions still to agree with TASK-05A before implementation, because they change a shared contract:

1. How a JavaScript body treats a shared binding that has no value (§4).
2. The exit for a local validation that fails only at publication (§5).
3. Whether a JavaScript item stores every `config.*`/`setting.*` value or the body stores one copy (§4).
4. An "every instance variable" request for a JavaScript body's snapshot (§4).
5. Ownership of the local-validation language rule that TASK-05A lists as an open finding (§6).

All five were agreed on 2026-11-12 with the proposals above; see the record below.

## Implementation record (2026-11-12)

The script adapter is implemented on the TASK-05A foundation. The status stays **In
progress**: the browser-acceptance, scale, and prepared-documentation gates listed
under [Still open](#still-open) are not complete.

### Decisions

The five questions above took the proposals this plan recorded. TASK-05A records them
under **Script adapter decisions** in its §8:

1. In a JavaScript body, a shared binding that the conservative staging scope alone
   reads, and that has never been set, is absent from the item context. A statically
   named dependency (a local default or validation) still fails closed.
2. A local validation that fails only against the shared values current at
   publication is the saved candidate's authored output failure. It follows the
   node's job policy and never evaluates the script again.
3. Each JavaScript item stores its own copy of every `config.*`/`setting.*` value.
   Measured on a Development stack with two workflow settings and no configuration
   values: **579 bytes** of staged input per item, so 1,000 items use about 0.6 MB in
   total and each item stays far under its 1 MiB limit.
4. The adapter lists a JavaScript body's stored instance-variable names under the
   instance lock and passes them as named inputs.
5. TASK-05C owns the local-validation language rule (see **Validation** below).

`[mi.item]` in NCalc (§2) keeps an ordinary script's conversion of each JSON kind
rather than adding a rule: `string`, `number`, and `boolean` arrive as their values,
`date` and `datetime` as their authored text, a `json` object as its JSON text, and a
`json` array as a list. Nothing is rejected at save; the editor's help says to use
JavaScript to read object properties.

### What changed

| Area | Change |
| --- | --- |
| Release boundary | `AutomaticMultiInstanceRules.InstalledAdapterKinds` holds `scriptTask`. Publication, runtime entry, and item processing each require the open switch and an installed kind (`WorkflowDefinitionReadinessChecker`, now given `AutomaticMultiInstanceOptions` through `WorkflowDefinitionService`; `EnsureAutomaticMultiInstanceRuntimeAvailable`, now given them by the engine; the item-job dispatch). The Worker resolves the switch for its own environment, as the API does. Service and call bodies stay refused at all three. |
| Routing | `WorkflowEngineService.Jobs.cs` sends a body's `asyncBefore` job and every `multiInstanceItem` job to one dispatch by node type in `WorkflowEngineService.AutomaticMultiInstanceScripts.cs`, the seam TASK-05B adds its case to. `FinalizeAsyncContinuationAsync` refuses a body's entry job. |
| Entry | Names the instance inputs to freeze (NCalc references; every stored name for JavaScript; never the aggregate target or the collection, which the snapshot holds once) and calls `InitializeBodyAsync`. A source failure follows the node's job policy: retry, the catch-all boundary with the stable code in its error variable, or an incident. The boundary is taken by `EnterAutomaticErrorBoundaryAsync`, the route a body's own error uses: Error-phase locks only, with the error target validated. |
| Sizes | `JsonbText.ByteCount` counts the bytes PostgreSQL prints for stored `jsonb`, which the size check constraints measure. Every automatic limit uses it (snapshot, staged input, staged input plus candidate, result, aggregate), so the code refuses a value before the database could. |
| Items | Stage (`CaptureItemContextAsync`, with the referenced or full `config.*`/`setting.*`/`sys.*` environment), evaluate with no transaction, save (`SaveCandidateAsync`), and publish (`PublishCandidateAsync`), each in its own transaction. An item with a saved candidate always goes straight to publication. |
| Item context | `EvaluateAutomaticScriptItem` uses its own `IScriptContext`, not `EngineScriptContext`. Writes resolve to declared locals, then to `readWrite` shared aliases outside the collection, aggregate, and error targets; every other target fails with a message naming it, and one shared key through two aliases fails at the second write. Fences narrow to the aliases written; the validation context holds only the names local validations reference. An item never reads its own aggregate target. |
| Failure exits | Script errors (`script_task_failed`), including NCalc division by zero and a number that is not finite, and output-contract failures (`multi_instance_result_invalid`) follow the node's job policy through `CaptureItemFailureAsync` or a retry, then a `job_execution_failed` incident. Size refusals use `RefuseItemResultAsync`. A shared value conflict opens `output_version_conflict` over the saved candidate. |
| Shared access | `SharedVariableAutomaticPhaseAccessPlan.OptionalReadAliases` carries decision 1; the planner and `WorkflowVariableStore.MergeEffectiveValuesAsync` use it. |
| Validation | Server (`FindItemExpressionProblem`, `ValidateFlowInfoUsage`, `ValidateGatewayExpressionUsage`) and editor (`automaticItemExpressionProblem`, `validateFlowInfoUsage`, `validateGatewayExpressionUsage`) reject `mi.*` names other than `mi.item`/`mi.index`/`mi.total` in local defaults, local validations, and NCalc body assignments, and `FlowInfo`, gateway helpers, and `CountFlow`/`PercentFlow` in local validations. Item-context messages are identical on both sides. |
| Editor | A body's NCalc assignment targets list its item-local outputs and writable shared aliases, rebuilt on focus, and keep an unavailable imported value visible. Script-format help, the JavaScript target note, and **Insert example** describe item work. Enabling multi-instance on a JavaScript task with a script asks the author to review its `setVariable` targets. |
| Documentation | [AGENTS.md](../../AGENTS.md), [API reference](../api-guide.md#get-apiworkflowsauthoring-capabilities), [deployment](../deployment.md#automatic-multi-instance-foundation), [UI guide](../ui-guide.md), [database schema](../database-schema.md#automatic-multi-instance-foundation), [TASK-05A](task-05a-multi-instance-core.md), the parent, and the backlog. |

### Verification

- New PostgreSQL scenarios (`AutomaticMultiInstanceScriptRuntimeTests`, 8/8) publish
  through an API host with the switch open and run every job through the registered
  processor: the NCalc sequential example (one item admitted at a time, exact
  aggregate), the JavaScript parallel example with items finished in reverse order,
  a caught failure (partial result `completed`/`failed`/`cancelled`, boundary taken),
  an uncaught failure (retried with the same staged input, then an incident, nothing
  published), a shared output conflict (incident over the saved candidate; retry
  publishes it, replacing the newer value, with one evaluation in total), an
  interrupting timer that fires during evaluation (body cancelled, late result
  fenced, no aggregate), an unset shared collection (entry failure takes the
  boundary), and service and call bodies refused at publication.
- Unit and parity tests: `AutomaticMultiInstanceScriptAdapterTests` (21) and
  `EditorAutomaticMultiInstanceScriptTests` (8). One foundation test that pinned
  script bodies as refused at runtime now pins only service and call bodies.
- A verification pass added seven PostgreSQL scenarios (15/15 pass): `asyncAfter` runs
  once after the aggregate; an empty collection completes with no evaluation;
  sequential items read shared values that earlier items committed; a JavaScript body
  stages while a binding was never set and writes it first; `retryFirst` retries
  before the boundary; a local validation failure takes the boundary with
  `multi_instance_result_invalid`; a validation that fails only at publication does
  too, after one evaluation; and a JavaScript body freezes every instance variable, so
  an oversized one fails entry where an NCalc body does not. A processor test pins a
  service body's entry job (retried with the adapter message) and item job
  (`job_invariant_violation`) as refused at runtime, and a unit test pins that a
  downstream call never reads a local.
- Focused regression before the last editor change: **2,076 passed, 1 failed** (the
  foundation test above, then updated), 2,077 total, in 5 minutes 10 seconds. Command:
  `dotnet test Flowbit/tests/Flowbit.Tests/Flowbit.Tests.csproj --no-build -m:1 --filter "FullyQualifiedName~AutomaticMultiInstance|FullyQualifiedName~Editor|FullyQualifiedName~Script|FullyQualifiedName~SharedVariable|FullyQualifiedName~Durable|FullyQualifiedName~DefinitionValidation|FullyQualifiedName~WorkflowAuthoringPackage|FullyQualifiedName~CallActivity|FullyQualifiedName~Incident|FullyQualifiedName~Retry"`.
- Full solution after the final code: **4,940 passed, 1 failed**, 4,941 total, in
  11 minutes 50 seconds. Command: `dotnet test Flowbit/Flowbit.slnx --no-build -m:1`.
  The failure, `WorkflowAiConversationTests.ATruncatedResponseAndTheDeadlinePauseTheRun`,
  hit that test's one-second mocked-client timeout under load; it is in AI authoring,
  which this change does not touch, and passed 3 of 3 runs alone.
- Real browser (built-in Chromium pane), Release-published API
  (`http://127.0.0.1:15617`, Development, switch open), Worker, and UI
  (`http://127.0.0.1:15652`) over a disposable PostgreSQL. In the hosted editor at
  1024×768: imported the NCalc example, saw both item-local outputs offered as
  assignment targets, changed a target and restored it with Ctrl+Z, typed
  `[mi.completed]` and got the new save error, corrected it, saved, published, and
  started. The Worker completed the body with the exact aggregate above, and item
  detail showed each copied item and its locals. The JavaScript example with a
  failing line completed two items and blocked one (**Admission paused by an item
  incident**, aggregate still `[]`); retrying the incident in **Jobs & incidents**
  re-ran only that item, which failed again over the same input. Instance detail had
  no page overflow at 1440×900 and 390×844, and the hosted script inspector showed its
  item targets at 1024×768 and 390×844. The console had no errors or warnings; the
  Worker logged only the expected script failures.

- Review fixes (2026-11-12, after a second review): an entry failure takes the boundary
  under Error-phase locks with its error target validated; NCalc division by zero and
  a number that is not finite are script failures, in ordinary scripts too; every size
  limit counts the stored `jsonb` text; the collection is frozen once; and runtime
  entry and item processing need the open switch, which the Worker now resolves. New
  tests: six PostgreSQL scenarios in `AutomaticMultiInstanceScriptRuntimeTests` (a held
  row lock on a `readWrite` binding no longer blocks the entry route; a refused error
  code waits in a `job_conflict` incident; `[mi.item] % 0` takes the boundary; 4,000
  copies of `1E+300`, 28 KB compact but about 1.2 MB stored, are refused once; a 600 KB
  collection runs; a closed host refuses the start and the entry and item jobs, and
  an open host then finishes), `JsonbTextTests` (28 values compared with PostgreSQL's
  own `octet_length(value::jsonb::text)`), four ordinary-script cases in
  `ErrorBoundaryMatchingRuntimeTests`, and adapter unit cases. The first full run
  after these tests failed 10 cases with PostgreSQL `53300: too many clients`: each
  API test host registered its connection pools as ready-made instances, which the
  container never disposes, so stopped hosts kept idle connections for five minutes.
  `AddInfrastructure` now lets the container build and dispose both pools
  (`InfrastructureConfigurationTests` pins it). Full solution then: **4,997 passed,
  0 failed**, in 12 minutes 46 seconds (`dotnet test Flowbit/Flowbit.slnx --no-build`).
  No editor or UI file changed, so no browser check was needed.

### Still open

Review findings (2026-11-12), confirmed by reading the code and not yet fixed:

- **JavaScript lock scope wording.** A JavaScript stage locks every shared binding of the
  definition for update, read-only ones and the aggregate included, not only the
  `readWrite` bindings that the help, AGENTS.md, and this record describe.
- **Smaller gaps.** Item-owner locks take the job before the item row (the instance lock
  serializes them, but AGENTS.md orders items first); the server finds `mi.*` names
  through the NCalc parser, so an unparseable expression or a name inside a string
  literal can give a different first error than the editor; an item that reads its own
  aggregate target passes save and then fails (NCalc) or reads nothing (JavaScript);
  switching script format on a body drops its assignments or script without a prompt;
  the body section's live status does not update while an assignment is typed; the
  help does not say that sequential items see earlier items' shared writes.

Tests the plan lists that do not exist yet: a staged-input size refusal for a script
item (a result over its limit is covered), FlowInfo and `usesFlowInfo` on a body, a shared collection's success path, conditional
boundaries and non-interrupting timers on a script body, missing or archived bindings
as incidents, several item incidents at once, no conditional wake from a shared write,
the automatic-loop guard, a snapshot just under 1 MiB, and NCalc failure rows under
both `failureHandling` values.

- Worker-enabled browser acceptance scenarios in
  `Flowbit/tests/Flowbit.BrowserAcceptanceTests/` for script bodies, with screenshots;
  the manual browser check above does not replace them.
- Script-specific cases for non-interrupting timer and conditional boundaries, a
  shared typed collection's success path, sequential shared visibility, and the
  JavaScript lock scope under several Workers. TASK-05A's common tests cover the
  boundary lifecycle itself.
- The 1,000-item scale run in both formats, recording duration and queries per item.
- The prepared script sections of the owning guides and the canonical example
  definitions (§9); the runtime tests hold equivalent fixtures for now.
