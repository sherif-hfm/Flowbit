# Stage 4 — Split editor save validation

[Plan index](README.md) · [Next: instance detail components](stage-05-instance-detail-components.md)

**Status: Implemented.** The save validator now builds a fresh validation
context per call and executes named phase functions and rule helpers; the
editor remains the single standalone HTML file. The implementation record at
the end of this document reports scope, tests, and browser verification.

## Objective and boundary

Make `validateModelForSave(candidate)` easier to understand and change by
extracting named validation phases and rule helpers. Keep the editor in
[flowbit-editor.html](../../flowbit-editor.html), with no build step, imported
modules, or external runtime dependencies.

The validator includes cross-node topology, process-variable, expression,
authorization-configuration, and multi-instance checks. Splitting only by node
type would obscure these dependencies and could reorder errors. Preserve the
current traversal order while extracting cohesive blocks.

Rendering, inspectors, save/load formats, normalization/migration, undo/redo,
pointer interactions, and validation policy changes are outside this stage.
Do not fix newly discovered validation discrepancies in the extraction diff;
record them for a separate behavior change.

## Target structure

Keep `validateModelForSave(candidate)` as the public entry point returning the
same ordered array of strings. Introduce these local implementation boundaries
inside the existing validator block:

| Proposed owner | Responsibility |
| --- | --- |
| `createSaveValidationContext(candidate)` | Per-call candidate collections, error accumulator, lookup maps, structural adjacency, and reusable predicates. |
| Named phase functions | Execute the existing validation blocks in their original sequence. |
| Node/flow rule helpers | Validate one configuration within a phase, such as service-task outputs, message settings, gateway topology, or multi-instance outcomes. |
| Existing expression/graph helpers | Continue to own expression recognition, reachability, and allowed-context rules. Move only what extraction requires. |

Use plain functions and a context object. Avoid a rule registry, class hierarchy,
plugin mechanism, global cache, or generic validation framework. Proposed helper
names describe internal ownership; they do not create a public API.

## Implementation sequence

1. Run the existing editor tests. Inventory the validator's current loops,
   early returns/continues, shared locals, map construction, and error emissions.
   Record the ordered results for representative valid and multi-error models
   before moving code.
2. Introduce the context with the same candidate-array fallbacks and predicates.
   Build lookups once per validation call, preserving original insertion and
   overwrite rules. Preserve later-phase initialization where moving it earlier
   would change error timing or how malformed input is handled.
3. Extract the earliest coherent validation blocks first. Suggested boundaries
   include attributes and roles, variable declarations/shared bindings, script
   and expression contexts, service/message/timer/boundary settings, graph and
   gateway rules, entry identity/business-key rules, and multi-instance rules.
   These are ownership groups, not permission to reorder the current passes.
4. Within each phase, extract per-node or per-flow functions where useful.
   Keep one existing traversal and call its helpers in the original order. Do
   not group all nodes of one type if that changes the sequence of messages.
   Run the focused tests after each moved block.
5. Keep all new helpers required by the extracted test harness between
   `// BEGIN WORKFLOW SAVE VALIDATOR` and `// END WORKFLOW SAVE VALIDATOR`.
   Preserve marker spelling and the entry-point name. Preserve any existing
   explicitly provided harness dependencies; add no accidental reliance on DOM
   state, selection, or the global `model`.
6. Remove superseded local closures only after all callers use the extracted
   owners. Review the diff for unchanged condition expressions and messages.
   Complete full tests and real-browser checks before marking the stage done.

## Invariants

- Return exactly the same errors in the same order, including duplicates,
  punctuation, labels, casing, and null/empty handling. Do not sort or deduplicate.
- Validate the supplied candidate. Do not mutate it or silently normalize it.
  Keep load-time normalization separate from save validation.
- Preserve case-insensitive variable/role rules where they currently apply and
  exact comparisons where required. Preserve Unicode length semantics per rule.
- Preserve `Map`/`Set` and traversal behavior for duplicate IDs and names; do not
  silently change which duplicate lookup wins while reporting existing errors.
- Keep graph reachability, attached boundary structural edges, gateway scope,
  `joinCancellation`, and scoped-interrupt checks consistent across phases.
- Preserve allowed expression contexts, quoted-text handling, `FlowInfo`, and
  multi-instance helper references. Preserve shared-variable access rules and
  the required engine-only default outcome.
- Context is per call. Validating a second model must not reuse maps, errors, or
  reachability from the first. Avoid rebuilding whole-model maps per node.

## Automated tests

Existing owners are
[EditorValidatorTests](../../Flowbit/tests/Flowbit.Tests/EditorValidatorTests.cs),
[EditorRuntimeSmokeTests](../../Flowbit/tests/Flowbit.Tests/EditorRuntimeSmokeTests.cs),
[EditorConditionalEventTests](../../Flowbit/tests/Flowbit.Tests/EditorConditionalEventTests.cs),
and [EditorNavigationTests](../../Flowbit/tests/Flowbit.Tests/EditorNavigationTests.cs).

Explicitly retain `InboxVisibilityCompiler_MatchesSharedConformanceCorpus` in
the editor validator tests and the server conformance cases in
[InboxVisibilityConditionCompilerTests](../../Flowbit/tests/Flowbit.Tests/InboxVisibilityConditionCompilerTests.cs).
Both use the existing
[inbox visibility corpus](../../Flowbit/tests/Flowbit.Tests/Fixtures/inbox-visibility-conformance.json).
The parser remains an available dependency inside the marked validator block;
rewriting it or generating a shared grammar is outside this extraction.

Reuse their test harness and example loading rather than add another JavaScript
runtime. Existing backend validation remains a compatibility reference; editor
and backend validators need not have identical diagnostic wording.

Add focused characterization cases where current tests only check message
containment: a model with errors in several phases must preserve the complete
ordered error array; repeated calls for different models must not leak context;
and validation must leave the input unchanged. Include representative gateway,
shared-variable, role-source, and multi-instance cases. Use the current
implementation to establish expected results before extraction; do not keep a
second production validator as a test oracle.

From the repository root, in PowerShell or Bash:

```text
dotnet test Flowbit/Flowbit.slnx --filter "FullyQualifiedName~EditorValidatorTests|FullyQualifiedName~EditorRuntimeSmokeTests|FullyQualifiedName~EditorConditionalEventTests|FullyQualifiedName~EditorNavigationTests|FullyQualifiedName~InboxVisibilityConditionCompilerTests|FullyQualifiedName~EditorValidatorCharacterizationTests"
docker info
dotnet test Flowbit/Flowbit.slnx --nologo --verbosity quiet
git diff --check
```

## Required browser verification

Serve the repository over loopback with a locally available static-file server
and open `http://127.0.0.1:8000/flowbit-editor.html` in a real browser. Record the
actual server command and URL; if port 8000 is occupied, use and report another
port. This is a development verification step, not a new application dependency.
Also reopen the final HTML directly as a file to confirm standalone operation.

Use real clicks, typing, keyboard navigation, and pointer input to:

1. Load the seed model and a valid curated workflow from the
   [example catalog](../../examples/README.md), save, reload, and confirm the
   authored model is preserved.
2. Use the inspector to make invalid node/flow settings, attempt save, and check
   the displayed message text/order against the baseline. Correct them and save.
3. Exercise examples with gateways/scoped interruption, multi-instance outcomes,
   and shared or role variables; make one relevant invalid change in each.
4. Switch between two models and validate both; exercise undo/redo around an edit.
   Move a node and a lane, connect nodes, then validate and save to confirm the
   extracted validator still receives current model state.
5. Inspect the inspector and validation feedback at **1440 × 900** and
   **1024 × 768** viewports. Confirm keyboard focus and visible feedback remain
   usable and compare layout with the existing editor.

Inspect the console for errors and warnings. Record browser/version, localhost
URL, interactions, viewport sizes, console result, and any limitation. Capture
before/after screenshots for comparison; include a screenshot in the final
implementation report if appearance changed. Jint results do not satisfy this
browser gate.

## Completion, documentation, and rollback

Acceptance requires unchanged ordered diagnostics for the characterization
cases, all focused/full tests passing, no new context leakage or model mutation,
and completed browser verification. No improvement target is based on line count.

Update the validator ownership explanation in [AGENTS.md](../../AGENTS.md).
Review [BPMN support](../bpmn-support.md), [node reference](../node-reference.md),
and [Flowbit/README.md](../../Flowbit/README.md) for any affected implementation
descriptions. The documented validation rules and JSON examples should remain
unchanged. Validate modified links and examples.

Deliver characterization coverage first if missing, then small buildable
extraction commits with their relevant test results. Mark the whole stage
implemented only after the final full-suite and browser gate. Revert the
extraction commits to roll back; saved workflow JSON requires no migration.

## Implementation record

Delivered as one buildable change to [flowbit-editor.html](../../flowbit-editor.html):

- `validateModelForSave(candidate)` remains the public entry point and returns
  the same ordered error array. It now builds a fresh
  `createSaveValidationContext(candidate)` (error accumulator, node/flow/variable
  collections, `nodesById`, incoming/outgoing/structural adjacency,
  `knownFlowIds`, `processByName`, `userTasksById`, workflow key, and lazy
  reachability helpers) and then calls named phase functions in the original
  sequence: attributes, workflow key, unique ids, role sources, task
  distribution, inbox visibility conditions, assignment settings, variable
  names, FlowInfo usage, gateway expression usage, shared bindings, script
  tasks, variable contracts, declared producers, async/job/timer metadata,
  service tasks, error boundaries, timer/conditional boundaries, message
  catches, timer catches, conditional events, message starts, entries, end
  events, node types, gateway topology, graph scopes (joinCancellation,
  scoped interrupt, flow condition priority), entry identity (idempotency and
  business keys), and multi-instance outcomes.
- Shared rule helpers — `validateTypedOutputMappings`,
  `validateRuntimeOutputTarget`, `validateMessageAuthentication`,
  `validateVariableContracts`, `validateDeclaredProducerVariables`,
  `validateRoleSource`, `validateUniqueIds`, `validateVariableNames`,
  `validateAttributes`, expression inspectors, and pure predicates such as
  `hasUnsupportedPassThroughMetadata` — moved to top level with the context or
  explicit parameters. The label helpers are `saveNodeLabel`/`saveFlowLabel`
  because `flowLabel(flow, source)` already exists outside the validator, and
  the shared-variable predicates are `saveIsSharedProcessVariable`,
  `saveIsWritableProcessVariable`, and
  `saveSharedVariablesReferencedByExpression` because the editor already defines
  canonicalizing helpers with those names at top level; the validator-local
  copies keep the strict pre-extraction semantics (exact `"shared"` scope text
  and no default-argument model lookup) and are used only inside the marked
  block.
- No validation policy changed: every traversal, condition expression, label,
  and message string is unchanged, and each phase keeps its original position
  in the error sequence. The parser, timer helpers, and Unicode helpers inside
  the `// BEGIN/END WORKFLOW SAVE VALIDATOR` markers are unchanged, and the
  harness dependencies (`normalizeRoles`) are unchanged.
- New `EditorValidatorCharacterizationTests` pin the complete ordered error
  arrays of two multi-phase models (attributes through gateways, and a
  gateway/shared/role-source/multi-instance model), assert per-call context
  isolation across alternating models, and assert via the Jint harness that
  validation never mutates the supplied candidate.
- Differential verification: the pre-extraction validator was recovered from
  git history and both implementations ran over 376 models — the two
  multi-error probes, the empty and reused-context probes, all 42 curated
  examples, and seven deterministic damaged variants of each (missing workflow
  id, removed start event, forced priorities, bogus data types, duplicated
  variable, stripped node configuration, altered expressions). Old and new
  validators returned identical ordered arrays for every model, and no variant
  mutated its input.

Validation (2026-09-18):

- Focused editor tests (`EditorValidatorTests`,
  `EditorRuntimeSmokeTests`, `EditorConditionalEventTests`,
  `EditorNavigationTests`, `InboxVisibilityConditionCompilerTests`, and
  `EditorValidatorCharacterizationTests`) passed 297/297 before extraction and
  301/301 after it. The full suite passed 1,873/0/0 with Docker PostgreSQL
  (one unrelated `WorkerDispatcherTests` timing case failed once and passed on
  re-run and in the final full-suite run). `git diff --check` is clean.
- Real-browser verification: Google Chrome 153.0.8010.50 (desktop, driven by
  `playwright-core` 1.63.0 with real input events) against
  `http://127.0.0.1:8000/flowbit-editor.html`, served by a small local Node
  static-file server from the repository root, plus a `file://` reopen of the
  final HTML. 42/42 checks passed (rerun after the helper rename): seed model
  and curated examples load and save (save via the File menu; the File System
  Access picker was stubbed to capture the serialized model — the download
  fallback was not separately exercised); save/reload round trips preserve the
  authored model; inspector edits (duplicate process variable, missing
  scoped-interrupt continuation, duplicate multi-instance completion priority,
  reserved shared catalog key) reproduce exactly the validator's ordered arrays
  in the modal; undo/redo, node and lane pointer drags, connect mode, model
  switching, and a 1024 × 768 viewport with modal keyboard focus all behaved;
  the only console entry was the browser's automatic favicon.ico 404.
  Screenshots recorded for both viewports; no visual change was introduced by
  the extraction.
- Documentation updates: the validator ownership bullet in
  [AGENTS.md](../../AGENTS.md), this record and status, and the stage status in
  the [plan index](README.md) with the [gap inventory](gaps.md) line reference.
  Validation-rule documentation ([BPMN support](../bpmn-support.md),
  [node reference](../node-reference.md), [Flowbit/README.md](../../Flowbit/README.md))
  is unchanged because the rules and diagnostics are unchanged.
