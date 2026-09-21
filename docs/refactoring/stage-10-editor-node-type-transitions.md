# Stage 10 — Isolate editor node-type transitions

[Plan index](README.md) · [Remaining-gaps roadmap](remaining-gaps-implementation-plan.md#stage-10--isolate-editor-node-type-transitions) · [Editor gap](gaps.md#5-editor-hotspots-outside-save-validation)

**Status: Implemented (2026-09-21).** Prepared on 2026-09-21 against commit
`239a30e`; the extraction was implemented on top of that plan with no upstream
changes to the editor beyond this stage. The transition body now lives in the
named `changeNodeTypeFromInspector(node, nextType)` helper immediately before
`renderNodeInspector()`, the selector adapter owns both redraw paths, and the
acceptance evidence below was produced against the final state. See the
[implementation record](#implementation-record) for commands, counts, and
limitations.

## Objective and scope

Move the graph-changing body of the Type selector callback out of
`renderNodeInspector()` into a named, synchronous
`changeNodeTypeFromInspector(node, nextType)` function in
[flowbit-editor.html](../../flowbit-editor.html). Keep the selector, its
accepted/rejected redraw decision, and all other inspector rendering in
`renderNodeInspector()`.

Preserve the existing behavior exactly: rejected and confirmed changes,
gateway metadata cleanup, role references, attached-boundary deletion,
message-start conversion, business-key defaults, default-start reassignment,
normalization order, and whole-model undo/redo. A successful extraction changes
ownership, not which workflows can be authored or saved.

The editor remains one dependency-free HTML file that opens directly in a
browser. Do not add modules, packages, a build step, a framework, a migration,
or a second production copy of the transition logic. HTTP contracts, runtime
execution, and saved workflow JSON remain unchanged.

Leave these areas unchanged:

- `applyTypeInvariants`, `applyFlowInvariants`, and their existing normalization
  helpers; call them at their current positions.
- The expression parser and the complete `// BEGIN/END WORKFLOW SAVE VALIDATOR`
  region, including its markers and ordered diagnostics.
- Other inspector sections, `NODE_TYPE_OPTIONS`, boundary creation controls,
  node deletion, loading/saving, history implementation, HTML markup, and CSS.
- Other engine, repository, and Blazor extractions, including Stage 11.

Stage 7's browser harness is available and provides the prerequisite browser
coverage. Stage 10 can proceed while Stage 7's remote CI observation and
Stage 5's residual acceptance remain open; it does not require Stage 5
acceptance or a new backend extraction. Coordinate edits to the browser harness
and shared documentation with any concurrent stage work.

## Source inventory and ownership

Locations refer to the preparation commit; re-find symbols before editing.
Proposed behavior below is a design, not an assertion that it already exists.

| Owner | Current responsibility | Stage 10 treatment |
| --- | --- | --- |
| `renderNodeInspector()` (line 5169) | Resolves selection, normalizes the selected node, builds fields, and embeds the Type callback at lines 5191–5285. | Retain all field construction; replace only the transition body with the thin adapter below. |
| `changeNodeTypeFromInspector(node, nextType)` (new) | No named transition owner exists. | Own the existing checks, prompts, graph mutations, normalization calls, and default-start repair; return acceptance. |
| `applyTypeInvariants()` / `applyFlowInvariants()` | Normalize node/flow metadata; node invariants can remove extra outgoing flows. | Reuse unchanged, preserving order and fresh outgoing-flow lookups. |
| `materializeMessageStartVariables()` / `normalizeLegacyMessageStartNode()` | Convert between message output mappings and ordinary start variables. | Reuse unchanged; retain the explicit materialization before changing away from a message start. |
| `render()` / `renderInspector()` | Normalize and redraw different portions of the editor. | Keep the accepted/full versus rejected/inspector-only choice in the selector adapter. |
| `selectField()` and document event listeners | Invoke the field callback, then commit history through existing events. | No production changes; the new helper does not commit history. |
| [EditorRuntimeSmokeTests](../../Flowbit/tests/Flowbit.Tests/EditorRuntimeSmokeTests.cs) | Executes the actual main inline script with Jint and DOM stubs. | Add transition characterization using the existing private harness. |
| [EditorSmokeTests](../../Flowbit/tests/Flowbit.BrowserTests/EditorSmokeTests.cs) | E1–E5 cover editor load/save, dragging, validation, and keyboard behavior. | Add focused Type-selector scenarios through real browser controls. |
| [BrowserScenario](../../Flowbit/tests/Flowbit.BrowserTests/Infrastructure/BrowserScenario.cs) | Captures diagnostics and accepts allowlisted dialog prefixes. | Add narrowly scoped expected-confirmation responses so Cancel can be tested without replacing browser dialogs. |

Place the new function immediately before `renderNodeInspector()`, outside all
existing marked extraction regions. Keep it at script scope, consistent with
the editor's other helpers. It receives the existing selected-node object and
the selector's type string and uses the same global `model` and helpers as the
current callback. Do not clone the model or re-resolve the node by ID inside it.

## Proposed function contract

The helper returns `false` only for either existing rejected transition and
`true` after all accepted-transition work finishes. `true` means the action was
accepted; it does not promise that the serialized model changed. There is no
new same-type early return, null guard, type canonicalization, or validation
policy.

Keep `alert()` and `confirm()` synchronous inside the helper, with their existing
messages and order. The helper must not call `render`, `renderInspector`,
`commitHistory`, `resetHistory`, or `loadFromObject`. Do not add exception
handling or an asynchronous confirmation abstraction during this extraction.

The selector adapter should have this shape:

```javascript
selectField("Type", NODE_TYPE_OPTIONS, node.type, nextType => {
  if (!changeNodeTypeFromInspector(node, nextType)) {
    renderInspector();
    return;
  }
  render();
});
```

Move the old callback body mechanically: rename its `v` parameter references
to `nextType`, replace the two rejected redraw/return paths with `return false`,
and replace the final full redraw with `return true`. Keep every remaining
statement, condition, traversal, deletion, and helper call in its original
order. Do not split this work into a collection of new cleanup abstractions.

The boundary-event branch remains a disabled Type text field. Boundary events
are created through host controls and remain absent from `NODE_TYPE_OPTIONS`.

## Behavior and ordering to preserve

### Guards and prompts

1. Before any transition mutation, reject an end-event target with one or more
   outgoing flows. This covers ordinary, error, and terminate end events through
   the existing predicate. Preserve this alert exactly:

   > Remove all outgoing sequence flows before changing this node to an end event.

2. Capture `businessKeysEnabled` from the current model: any ordinary or message
   start with `businessKey != null`. Capture `previousType` and compute gateway
   semantics change only when the type differs and either type is a gateway.
3. For a gateway semantics change, check outgoing flows for a truthy default,
   non-null condition or priority, nonempty normalized literal roles, or
   nonempty variables. If any match, preserve this confirmation exactly:

   > Changing this node's gateway semantics clears outgoing defaults, conditions, priorities, roles, and variables. Continue?

4. A declined confirmation returns before transition mutation. The adapter
   redraws the inspector so the visible Type value returns to its prior value.
   The end-event rejection precedes the gateway prompt when both could apply.

Preserve the precise confirmation predicate. A non-null empty condition still
qualifies. `rolesVariable`, claim-bypass metadata, and MI completion metadata
alone do not trigger this prompt today. Do not broaden the warning or add new
confirmation steps for boundary or outgoing-flow removal.

### Accepted mutation sequence

Execute the following steps in this order, after the guards above:

1. For message start → ordinary start, set `node.variables` with
   `materializeMessageStartVariables(node)` while the original message mappings
   still exist. This preserves mapping order, generated IDs `index + 1`, name,
   data type (missing defaults to `json`), array/required flags, default value,
   and validation. Reverse conversion remains in unchanged node normalization.
2. Assign `node.type = nextType`.
3. For any non-user-task target, delete `node.rolesVariable` and each outgoing
   flow's `rolesVariable`. This happens before outgoing-flow pruning.
4. For any non-gateway target, delete the node's own `joinCancellation` and null
   `joinCancellation.gatewayRef` on other nodes that reference this node, using
   `joinCancellationGatewayRef`. Leave unrelated references untouched. This is
   not a new cleanup pass for scoped-interrupt `gatewayRef` properties.
5. Entering a timer type from a non-timer seeds
   `{ timeDate: null, timeDuration: "PT1H", timeCycle: null }`. Timer → timer
   preserves its existing configuration for normal normalization. Entering a
   conditional catch from another type seeds `{ condition: "" }`.
6. For a gateway semantics change, set every outgoing flow's `isDefault` to
   `false`, `condition` and `conditionPriority` to `null`, and `roles` and
   `variables` to empty arrays.
7. If business keys were already enabled and the target is an ordinary or
   message start without a business key, initialize
   `{ variable: "", uniqueness: "active" }`. Preserve existing settings; timer
   starts do not participate in this rule.
8. Call `applyTypeInvariants(node)`, then obtain the outgoing flows again and
   apply `applyFlowInvariants` to them. Node normalization may replace
   `model.sequenceFlows`: single-outgoing types retain the first outgoing flow
   in array order, not the lowest ID. Never reuse a cached pre-normalization
   array for the second operation.
9. If the target is neither service nor script task, remove attached error
   boundary nodes and all flows with either endpoint in that boundary-ID set.
10. Separately determine timer/conditional boundary-host eligibility **after**
    node normalization. Retain these boundaries for user tasks, message catches,
    and timer catches, or for automatic/service/script tasks whose
    `asyncBefore === true`. Otherwise remove the attached timer/conditional
    boundary nodes and all their incident flows. Conditional catches are not in
    the current eligible-host set. Leave other hosts' boundaries intact.
11. If this node was `model.initialEventId` and is no longer an ordinary start,
    select the first other ordinary start in model array order, or `null` when
    none exists. A new start does not automatically become the default when the
    current default was already null or pointed elsewhere.
12. Return `true`; the inspector adapter then calls the existing `render()`.

Message-start conversion is asymmetric. Returning an ordinary start to a message
start omits optional variables without configured defaults and variables matching
the entry's idempotency variable; regenerated mappings have an empty `path`.
The original message configuration was deleted on conversion to an ordinary
start, so it is rebuilt with defaults on return. Characterize these existing
omissions and resets rather than requiring a lossless type round-trip.

### Redraw, persistence, and history

`render()` is not purely visual. It synchronizes selection, updates the workflow
name field, applies node invariants across the model, normalizes exclusive
gateway priorities, applies flow invariants across the model, redraws diagram
layers, rebuilds the inspector, and refreshes the hint. The selected node is
normalized again when its inspector is built. Preserve these calls and their
order; do not replace the full redraw with `renderDiagramLayers()`.

For example, the transition initially clears gateway priorities, but the later
full render may populate exclusive-gateway priorities. Tests must assert the
final user-observable model after the existing redraw, not a partially mutated
intermediate snapshot. `renderInspector()` can also normalize its selected node;
rejection tests should establish an already-normalized baseline first.

Keep node/flow IDs, names, external IDs, attributes, lane membership, model
metadata, and ordering wherever the existing transition/invariants retain them.
Compare boundary positions after the existing layout pass rather than promising
that attached-event coordinates never move. No new save/load normalization is
part of the change.

History stays with document `change`, click, and pointer handlers. An accepted
conversion, including removed boundaries/flows and default-start repair, remains
one whole-model undo step. A rejected conversion on a normalized fixture adds no
step and does not clear redo. Use a settled history baseline when asserting
this; a pending name edit can legitimately create its own entry. Undo replaces
the model object, so subsequent test actions must reacquire the current node or
selector callback rather than retain stale references.

## Implementation sequence

### 1. Record baselines and characterize existing behavior

1. Record the current commit and working-tree status. Re-find the callback and
   its dependencies; account for changes since `239a30e` before moving code.
2. Run the solution baseline with Docker available and the existing standalone
   Chromium suite using freshly built test assemblies and published hosts.
   Record actual counts and failures; historical Stage 9 results are not a pass
   for this candidate.
3. Capture the untouched inspector and representative transition outcomes at
   both editor viewports for later comparison. Record the commit/browser/URL.
4. Add missing characterization cases through the current Type callback, using
   the matrix below. Run them successfully **before** the extraction.
5. Extend expected-dialog handling only as needed for browser cancellation and
   pin that harness behavior with its own regression cases.

### 2. Extract the transition owner

1. Add the function and return contract described above.
2. Replace only the inline Type body with the adapter. Keep the initial
   `applyTypeInvariants(node)` in `renderNodeInspector()` and the boundary field
   branch in place.
3. Inspect the diff side by side: the intended application changes are the
   function boundary, parameter name, return values, and selector delegation.
   There should be no copied second callback, changed prompt text, or reordered
   normalization/cleanup.
4. Run the characterization and existing editor/parser suites. Investigate any
   difference against the pre-extraction result; record discovered behavior
   defects separately instead of silently fixing them in this stage.

### 3. Verify integration and finish documentation

1. Run the new browser transition scenarios through the actual selector at both
   viewports. Include save/reload and history restoration, not only SVG shape
   changes.
2. Perform the scenario-specific headed inspection below, then run the final
   full solution and complete standalone browser suite.
3. Update the ownership and stage documentation, validate changed references
   and fixture JSON, and run `git diff --check`.
4. Record results and remaining limitations in this stage document. Mark Stage
   10 implemented only when its acceptance checklist is complete.

## Automated characterization

### Existing test owners and harness rules

Add transition tests to `EditorRuntimeSmokeTests` so they reuse
`CreateEditorEngine`, its DOM stubs, and the build-copied real HTML. The existing
`UserTaskInspectorRendersWithoutLeakingTypeChangeCallbackState` remains a useful
regression for accidental callback state leaking into inspector construction.

Capture the Type callback by temporarily wrapping `selectField` while building
the node inspector, then restore the original builder before invoking the
captured callback. This exercises the same entry point before and after
extraction. Override only prompt responses/recording in the test environment;
retain the real invariants and full redraw for model assertions. Use fresh
engines/fixtures to isolate cases.

The current fake `dispatchEvent` does not invoke an element's `onchange`, and
assigning fake `innerHTML` does not clear children. Do not assume full DOM/event
semantics. Explicitly invoke the captured callback, and for Jint history checks
replay the registered document `change` event after it; real-browser tests prove
the actual bubbling and history path. Do not duplicate `DomStubs`, paste the old
transition into an oracle, or introduce another JavaScript runtime.

Retain the existing
[EditorValidatorTests](../../Flowbit/tests/Flowbit.Tests/EditorValidatorTests.cs),
[EditorValidatorCharacterizationTests](../../Flowbit/tests/Flowbit.Tests/EditorValidatorCharacterizationTests.cs),
[EditorConditionalEventTests](../../Flowbit/tests/Flowbit.Tests/EditorConditionalEventTests.cs),
and [EditorNavigationTests](../../Flowbit/tests/Flowbit.Tests/EditorNavigationTests.cs).
Run both the editor `InboxVisibilityCompiler_MatchesSharedConformanceCorpus`
test and [InboxVisibilityConditionCompilerTests](../../Flowbit/tests/Flowbit.Tests/InboxVisibilityConditionCompilerTests.cs)
against their existing shared corpus. The marked validator and parser stay
unchanged even though the extracted function sits elsewhere in the same file.

### Required behavior matrix

Use parameterized cases where the rule is shared. Add only missing coverage;
the rows describe risks to prove, not a required one-test-per-row structure.

| Case | Fixture/action | Required assertions |
| --- | --- | --- |
| T1 — rejected end targets | A node with outgoing flow(s) → ordinary/error/terminate end. Include a gateway source. | Exact alert; no gateway confirmation; prior type and complete model preserved after inspector redraw; selector restored in browser. |
| T2 — accepted end targets | A node with no outgoing flows → each end variant. | Target type and type-specific normalized fields; ordinary metadata cleanup matches baseline. |
| T3 — gateway cancellation | Enter, leave, or switch gateway type with routing metadata; decline. | One exact prompt, unchanged normalized graph, no full redraw, no new history or lost redo. |
| T4 — gateway acceptance | Accept each transition family with defaults/conditions/priorities/roles/variables. | Final node and flow JSON match baseline, including post-render exclusive priorities; incoming/unrelated flows retain their baseline data. |
| T5 — prompt edge cases | Each prompt-trigger field alone; clean outgoing flows; dynamic roles or claim/MI metadata alone. | Preserve existing prompt/no-prompt decisions, including non-null empty conditions and ignored metadata. |
| T6 — role references | User task with node and outgoing `rolesVariable` → non-user target; retain a user-task case. | Exact property deletion/preservation, distinct from literal role normalization; no changes to unrelated role sources. |
| T7 — gateway references | Gateway with its own `joinCancellation` and other nodes referencing it → non-gateway; gateway → gateway control. | Own metadata removed and matching join references nulled only when required; unrelated joins and scoped-interrupt references follow baseline. |
| T8 — outgoing pruning | Multiple outgoing flows in deliberately non-ID order → single-outgoing task/event type. | First array-ordered flow survives and is normalized; extra flows removed; IDs/attributes of retained objects preserved. |
| T9 — error boundaries | Service ↔ script, then host → unsupported type. | Error boundary retained for supported targets; otherwise boundary plus all incident flows removed; another host untouched. |
| T10 — observable boundaries | Timer and conditional boundaries on durable waits and async automatic/service/script hosts; change to supported/unsupported targets, including conditional catch. | Eligibility uses normalized `asyncBefore`; retained boundary settings survive; unsupported boundaries and all incident flows removed. |
| T11 — message/start conversion | Message start with typed output mappings → ordinary start and back; include optional/no-default and idempotency variables. | Forward materialization preserves mapping contracts/order and generates variable IDs; reverse conversion preserves existing omission rules, empty mapping paths, and reset message configuration. |
| T12 — entry identity | Conversion with/without business keys, existing key settings, and default start with zero/multiple alternatives. | Business-key initialization/preservation/removal follows baseline; first remaining ordinary start or null chosen; timer/message starts never substituted as default. |
| T13 — timer/conditional defaults | Non-timer → timer; timer → timer; timer → other; non-conditional → conditional catch; same-type conditional case. | Default `PT1H` only on entry from non-timer; existing timer and conditional settings retained where expected; unsupported settings removed. |
| T14 — user-task metadata | Configured MI/claim/assignment/inbox-visibility task → other type and a representative return to user task. | Existing node/flow invariants clear or initialize the same metadata, including engine-only outcomes and claim-bypass values. |
| T15 — same type and sequential actions | Invoke current type; perform several conversions, undo, then convert again. | No added same-type shortcut; no stale node references or cross-case state; same final graph and history as baseline. |
| T16 — save and restoration | Accepted destructive conversion, cancelled conversion, undo/redo, serialize/reload. | Whole graph restored in one step for accepted changes, rejected edit adds none, deleted properties remain absent, and saved-model cleanup round-trips. |

Compare complete serialized model structure for representative destructive and
rejected cases, with explicit assertions for absence versus null and array
order. Preserve exact prompt text. For valid final fixtures, require empty save
validation errors and a stable load/save round-trip. For intentionally invalid
intermediate states (for example a new blank conditional expression or cleared
gateway conditions), assert existing validation feedback; do not demand that
every accepted type change yields a saveable workflow without further editing.

## Real-browser verification

### Extend the existing suite

Reuse [EditorInteractions](../../Flowbit/tests/Flowbit.BrowserTests/Support/EditorInteractions.cs),
`BrowserStackFixture`, fresh `BrowserScenario` contexts, the copied editor, and
the existing serial execution/diagnostics. Add scenarios in `EditorSmokeTests`
using `EditorViewports`: **1440 × 900** and **1024 × 768**. Do not add another
host, package, or solution membership change.

`ExpectDialogStartingWith` currently accepts matching dialogs; it cannot model
an expected Cancel. Add a small test-only queue of one-shot expectations with
exact dialog type/message and accept/dismiss response. Check queued exact
expectations before the existing fallback-prefix handling. Fail unmatched
dialogs and unconsumed one-shot expectations; keep existing fallback-save
expectations working across repeated saves. Record consumed responses in
diagnostics. Add regression coverage in
[HarnessDiagnosticsTests](../../Flowbit/tests/Flowbit.BrowserTests/HarnessDiagnosticsTests.cs)
for expected accept, expected dismiss, and missing/unexpected dialogs. Keep one
dialog owner per page; do not add a competing handler or replace `window.confirm`.

Group the browser cases into these scenarios:

| Scenario | Real interaction and evidence |
| --- | --- |
| E6 — guarded changes | Select a node, attempt an end conversion with outgoing flows, dismiss the alert, then exercise gateway Cancel and Accept. Assert restored/updated selector values, node shapes, edge metadata, and no unexpected dialogs. |
| E7 — destructive conversion and history | Convert a host with attached boundaries and/or multiple outgoing flows. Inspect removed and surviving nodes/edges; undo and redo using real controls/shortcuts; save the valid result and reload the downloaded JSON in a fresh page. |
| E8 — start-event and settings conversion | Convert a typed message start to an ordinary start and back, inspect variables/mappings and default-start selection; exercise timer/conditional defaults through the inspector and complete required settings before saving. |

Include at least one accepted change through keyboard operation of the Type
select, and a rejected edit with an existing redo entry. Click a boundary and
verify its disabled Type field remains read-only. Exercise the existing node
and lane drag, connection, validation, and keyboard smoke coverage after the
refactor.

Use file chooser loading and real clicks, typing, selection, keys, and pointer
gestures. Do not call the new helper or mutate application globals from browser
evaluation. Read-only inspection can supplement DOM assertions; persisted JSON
from the existing download helper provides the save contract. Explicitly allow
only the expected dialogs. Reacquire locators after inspector rebuilding.

Add minimal fixtures under `Flowbit/tests/Flowbit.BrowserTests/Fixtures/` if the
current fixtures cannot express the cases. Document their purpose, initial
validity, and expected transitions in
[Fixtures/README.md](../../Flowbit/tests/Flowbit.BrowserTests/Fixtures/README.md).
Use independent models for incompatible scenarios; do not expand curated
examples merely to serve tests. Update the browser README's matrix and counts
from the actual implemented cases, preserving the historical Stage 7 record.

### Headed inspection and evidence

Serve the final repository editor over loopback and use a real desktop browser.
For example, if Python is available, this command works in PowerShell and Bash:

```text
python -m http.server 8000 --bind 127.0.0.1
```

Open `http://127.0.0.1:8000/flowbit-editor.html`; use another free port or existing
static server if necessary and record its actual command and URL. This is only
a development verification server, not a new editor dependency.

At both required viewports, repeat the gateway Cancel/Accept, rejected end,
boundary cleanup, start conversion, and undo/redo interactions. Inspect the
inspector's rebuilt controls, selected node, connectors, keyboard usability,
and before/after layout. After a valid conversion, save/reload through the
available browser path. Where supported, exercise native picker success and
Cancel separately from the automated download fallback. Record unsupported or
unexecuted paths explicitly; do not claim that the fallback proves native-picker
behavior or closes Stage 7's residual evidence.

Reopen the final HTML directly from disk to confirm standalone operation.
Capture before/after screenshots and console errors/warnings; link screenshots
in the implementation record, and include one in the final response if
appearance changed. No intended appearance change exists; investigate any
unexplained difference. Jint and DOM stubs do not satisfy this browser gate.

## Commands and acceptance evidence

These are **future implementation commands**, not runs performed to write this
plan. Run from the repository root. The single-line commands below work in
PowerShell and Bash; browser installation requires PowerShell 7 (`pwsh`).

Baseline and final solution checks:

```text
git status --short
git rev-parse --short HEAD
docker info
dotnet test Flowbit/Flowbit.slnx --nologo --verbosity quiet
```

Focused checks during extraction:

```text
dotnet test Flowbit/Flowbit.slnx --filter "FullyQualifiedName~EditorRuntimeSmokeTests|FullyQualifiedName~EditorValidatorTests|FullyQualifiedName~EditorValidatorCharacterizationTests|FullyQualifiedName~EditorConditionalEventTests|FullyQualifiedName~EditorNavigationTests|FullyQualifiedName~InboxVisibilityConditionCompilerTests" --nologo --verbosity quiet
```

Build/install the standalone browser suite as documented in its
[README](../../Flowbit/tests/Flowbit.BrowserTests/README.md):

```text
dotnet publish Flowbit/src/Flowbit.Api/Flowbit.Api.csproj -c Release -o artifacts/browser/hosts/api /p:UseAppHost=false
dotnet publish Flowbit/src/Flowbit.Ui/Flowbit.Ui.csproj -c Release -o artifacts/browser/hosts/ui /p:UseAppHost=false
dotnet build Flowbit/tests/Flowbit.BrowserTests/Flowbit.BrowserTests.csproj -c Release
pwsh Flowbit/tests/Flowbit.BrowserTests/bin/Release/net10.0/playwright.ps1 install chromium
dotnet test Flowbit/tests/Flowbit.BrowserTests/Flowbit.BrowserTests.csproj -c Release --no-build --no-restore --filter "FullyQualifiedName~EditorSmokeTests|FullyQualifiedName~HarnessDiagnosticsTests"
```

On Linux use `install --with-deps chromium` for the install step. Rebuild after
editing the HTML or tests: `--no-build` uses the copied editor from the preceding
build, not necessarily the current root file. The shared browser fixture still
requires Docker and both published hosts even for an editor-only filter.

Final complete browser gate and diff check:

```text
dotnet test Flowbit/tests/Flowbit.BrowserTests/Flowbit.BrowserTests.csproj -c Release --no-build --no-restore --logger "trx;LogFileName=stage-10-browser.trx" --results-directory artifacts/browser/stage-10-test-results
git diff --check
```

Retain the generated `artifacts/browser/runs/<timestamp>-<id>/` manifest,
per-scenario diagnostics, downloaded models, screenshots, and failure evidence.
Record exact commands, commit, passed/failed/skipped counts, browser/version,
localhost URL(s), viewports, console results, manual interactions, and artifact
paths. Report flakes/retries and unavailable checks explicitly. A test count or
historical pass does not establish acceptance.

## Documentation work

During implementation:

- Update the inspector ownership description in [AGENTS.md](../../AGENTS.md)
  to name the transition helper and explain the retained redraw/history owners.
- Update this plan with the implementation record and the
  [refactoring index](README.md), [roadmap](remaining-gaps-implementation-plan.md),
  [gap inventory](gaps.md), and [documentation home](../index.md) with the actual
  Stage 10 status. Keep parser/type-invariant decomposition and the other gaps
  explicitly deferred.
- Update the browser suite README and fixture catalog for the actual new
  scenarios, dialog response support, and fixture behavior.
- Review [node reference](../node-reference.md), [BPMN support](../bpmn-support.md),
  [developer guide](../developer-guide.md), [runtime reference](../../Flowbit/README.md),
  and [root README](../../README.md) against the final diff. Public behavior and
  JSON rules should remain accurate without edits; change only explanations
  made inaccurate by the implementation. Preserve unrelated product images.
- Validate changed relative links, anchors, image references, and added JSON
  fixtures. Report documentation updates with the validation results.

This planning change adds the detailed plan and discovery links only. It does
not update contributor ownership to describe the proposed helper as implemented.

> Implementation note (2026-09-21): the paragraph above describes the plan
> preparation; the [implementation record](#implementation-record) now supersedes
> it — the helper is implemented and contributor ownership (AGENTS.md) names it.

## Acceptance checklist

- [x] Fresh baseline recorded; characterization cases pass against the old callback.
- [x] One named transition helper owns the existing transition behavior; the Type adapter owns both redraw paths.
- [x] Prompts, mutation order, array ordering, role/reference cleanup, boundary eligibility, start conversion, and default selection match the baseline.
- [x] Node/flow invariants, parser, save-validator region, other inspector sections, HTML markup/CSS, and history implementation are unchanged.
- [x] Focused editor/parser tests and final full solution suite pass, with counts and limitations recorded.
- [x] Standalone Chromium suite, new transition scenarios, and dialog-harness regressions pass against a fresh editor build.
- [x] Scenario-specific headed localhost checks at 1440 × 900 and 1024 × 768, direct-file check, console results, and visual evidence are recorded.
- [x] Accepted conversions restore the entire graph with one undo/redo step; rejected/cancelled changes preserve history and redo on settled fixtures.
- [x] Saved-model round-trips and expected invalid intermediate-state feedback match the current behavior.
- [x] Documentation, fixture/reference validation, and `git diff --check` are complete; no unrelated gap is marked resolved.

If an acceptance check cannot run, leave the corresponding gate open and state
the limitation. Passing unit tests alone does not mark this UI stage accepted.

## Implementation record

Implemented on 2026-09-21 against the plan prepared at `239a30e`. Application
change: `flowbit-editor.html` gained `changeNodeTypeFromInspector(node, nextType)`
immediately before `renderNodeInspector()` (a mechanical move of the former
inline Type-callback body: `v` → `nextType`, rejection paths return `false`,
the accepted path returns `true` instead of calling `render()`), and the Type
selector delegates through the thin adapter prescribed by the plan. No other
editor behavior, markup, CSS, or validator-region change exists in the diff.

### Baseline and characterization

- Baseline commit `239a30e` (plan preparation), working tree carrying only the
  Stage 10 plan documents; `dotnet test Flowbit/Flowbit.slnx` passed 1,930/1,930
  before any test additions.
- 20 characterization tests (`TypeTransition_*` in
  `EditorRuntimeSmokeTests`) were added and passed **before** the extraction
  (20/20 at the old inline callback), covering the T1–T16 matrix: rejected end
  guards for all three end variants with exact alert text and inspector-only
  redraw, accepted end normalization, gateway decline (exact prompt text,
  unchanged model, unchanged history, preserved redo) and accept (cleared
  routing metadata, exclusive priorities repopulated by the full render,
  gateway→gateway joinCancellation retention), the prompt predicate
  (isDefault/non-null empty condition/priority/roles/variables trigger;
  clean flows, rolesVariable, claim-bypass, and MI metadata do not), role
  reference deletion only when leaving user tasks, joinCancellation ref nulling
  only when leaving gateways, first-array-order outgoing pruning, error and
  observable boundary eligibility (normalized `asyncBefore`, conditional
  catch hosts ineligible), message-start forward materialization and reverse
  rebuild with omission rules, entry identity/business-key defaults, timer and
  conditional defaults, same-type plus sequential conversions with undo/redo,
  save round-trip stability, and invalid intermediate-state validation
  feedback. The harness captures the live callback by temporarily wrapping
  `selectField`, records `alert`/`confirm`, and replays the document `change`
  event for history assertions.
- Verification-pass strengthening (post-extraction, verified against the final
  state): (a) the gateway-entry family now also covers a non-gateway →
  gateway transition with configured outgoing roles/variables, asserting both
  Cancel (exact prompt, type and model preserved, no full redraw) and Accept
  (roles/variables cleared, post-render exclusive priorities repopulated) —
  this pins the target-gateway side of the semantics check, which the original
  enter case exercised only with clean flows; (b) the message-start fixture's
  idempotency variable now names a mapping that carries a configured default,
  so the reverse conversion's idempotency exclusion is no longer masked by the
  optional-no-default rule (a separate optional variable still pins that
  rule). Both strengthenings were proven sensitive by temporary mutation runs:
  removing the target-gateway check fails the gateway-entry assertions, and
  removing the idempotency exclusion fails the message-start assertions; both
  mutations were reverted and the suite re-ran green (20/20).
- One pre-existing source-pattern assertion in
  `EditorValidatorTests.PlainEndEvent_RendersWithoutAnInnerIconAndConversionIsGuarded`
  pinned the old inline text `isEndEventType(v) && ...`; it was updated to the
  extracted source shape (`isEndEventType(nextType) && ...` plus the new
  helper's declaration). No behavior assertion changed.

### Commands and results (final state)

- `dotnet test Flowbit/Flowbit.slnx --nologo --verbosity quiet` — **passed
  1,950/1,950** (1,930 baseline + 20 characterization).
- Focused filter (`EditorRuntimeSmokeTests|EditorValidatorTests|EditorValidatorCharacterizationTests|EditorConditionalEventTests|EditorNavigationTests|InboxVisibilityConditionCompilerTests`)
  — **passed 321/321**, including the shared inbox-visibility conformance
  corpus.
 - Pre-change standalone Chromium baseline (recorded during the verification
  pass by rebuilding the suite against the pre-extraction editor via a
  temporary stash of `flowbit-editor.html`): the pre-existing scenarios
  (E1–E5 at both viewports plus R1–R6) ran **17 cases — 16 passed with one
  pre-existing `R2_InstanceNavigation` scroll-into-view flake that passed on
  an immediate retry**. This matches the flake family later seen on R6 in the
  full final-state run and confirms it is unrelated to the editor change.
- Browser suite final state (published API/UI hosts, Docker PostgreSQL, fresh
  editor build): `dotnet test Flowbit/tests/Flowbit.BrowserTests/Flowbit.BrowserTests.csproj
  -c Release --no-build --no-restore --filter "FullyQualifiedName~EditorSmokeTests|FullyQualifiedName~HarnessDiagnosticsTests"`
  — **passed 20/20** (E1–E8 at both viewports plus the four harness
  regressions). New scenarios E6 (guarded changes with queued
  alert-dismiss/confirm-cancel/confirm-accept plus a keyboard-operated
  accepted conversion), E7 (destructive conversion, keyboard undo/redo, a
  rejected edit with an existing redo entry that preserves the redo, disabled
  boundary Type field, save/reload), and E8
  (message-start round trip with rebuilt settings, timer/conditional defaults,
  default-start repair, save) pass at 1440×900 and 1024×768 against the real
  copied editor. Full-suite run with all product scenarios: **27 cases —
  26 passed and one flake**: `InstanceDetailSmokeTests.R6_ResponsiveControlsAndFocus`
  hit a scroll-into-view timing failure in the Blazor instance-detail page
  (code untouched by this stage) and passed on an immediate retry; every
  editor and harness case passed.
- The plan's full-suite TRX step was executed as
  `dotnet test Flowbit/tests/Flowbit.BrowserTests/Flowbit.BrowserTests.csproj -c Release --no-build --no-restore --logger "trx;LogFileName=stage-10-browser.trx" --results-directory artifacts/browser/stage-10-test-results`
  — 27 cases, 26 passed with the same unrelated R6 scroll-timing flake
  (passed on retry); artifacts remain under `artifacts/browser/runs/`
  (diagnostics per scenario) and `artifacts/browser/stage-10-test-results`.
- `git diff --check` — clean.

### Verification-pass findings and remediation

A post-implementation audit against this plan found and fixed two coverage
gaps and closed two evidence gaps; two process limitations remain recorded:

- Fixed (coverage): the automated E6–E8 scenarios initially lacked the plan's
  required "accepted change through keyboard operation of the Type select"
  (evidenced only in the headed inspection) and the "rejected edit with an
  existing redo entry" (evidenced only in Jint characterization). E6 now
  drives an accepted conversion through keyboard operation of the Type
  select, and E7 attempts a rejected end conversion while a redo entry
  exists, asserting the selector is restored, no history is consumed, and the
  redo still re-applies. Both were re-run green
  (`E6_Guarded|E7_Destructive`: 4/4).
- Fixed (coverage): the gateway-entry transition family now covers a
  non-gateway → gateway conversion with configured outgoing roles/variables
  (Cancel and Accept, prompt/cleanup/history assertions), and the
  message-start idempotency exclusion is no longer masked by the
  optional-no-default rule — both proven sensitive by the temporary mutation
  runs described above.
- Closed (evidence): the missing pre-change Chromium baseline was recorded by
  rebuilding the suite against the pre-extraction editor (17 cases, 16 passed,
  one pre-existing R2 scroll flake retried green), and the headed inspection
  now includes boundary cleanup and a fallback save/reload round trip at both
  viewports.
- Limitation: the **native** file-picker success/cancel paths for save remain
  a manual-only check (Stage 7's open item); the headed save/reload used the
  automated fallback path and does not close that residual.
- Original verification limitation: the implemented state was an uncommitted
  working tree on top of `239a30e`; that record cites the baseline commit and
  working-tree state. The follow-up below is based on committed `a9bcf57`.
- Remediation hygiene note: while recording the pre-change baseline above, a
  `git stash pop` rewrote `flowbit-editor.html` with CRLF endings, which broke
  one multi-line source-pattern assertion in
  `EditorConditionalEventTests`; the file was restored to its original LF
  endings (content unchanged — the extraction diff remained exactly two
  hunks), after which the full solution suite passed 1,950/1,950 again and the
  editor+harness browser filter passed 20/20 with the strengthened E6/E7
  scenarios. No product or test behavior was affected by the excursion.

### Headed inspection

A temporary verification driver (not part of the repository) served the final
repository `flowbit-editor.html` over `http://127.0.0.1:8791/flowbit-editor.html`
and drove headed Chromium 151.0.7922.34 with real pointer clicks, file-chooser
loading, select interactions, and keyboard input at **1440×900 and 1024×768**:
rejected end conversion (alert dismissed, selector restored), gateway
Cancel/Accept with edge metadata before/after, keyboard undo/redo, disabled
boundary Type field, **boundary cleanup** (converting the service-task host
removes its attached error boundary), message-start round trip with the
rebuilt settings retyped, timer PT1H seeding, a keyboard-driven accepted Type
change (Arrow keys on the focused select; the rebuilt inspector resets focus
by design), and a **fallback save/reload round trip** (the driver blocks
`showSaveFilePicker` exactly like the automated suite, saves through the
existing download fallback, and reloads the downloaded JSON in a fresh page,
asserting the converted model persisted with the boundary gone). Screenshots
and the step log live in `artifacts/browser/stage-10-headed/` and the
direct-file check opened `flowbit-editor.html` via `file://` successfully.
Console and page errors: none (the favicon 404 was eliminated by the
verification server, not by a product change).

Not exercised in headed mode (recorded limitation, unchanged from Stage 7's
open item): the **native** file-picker success/cancel paths for save remain a
manual-only check; the headed save/reload used the same fallback path as the
automated suite and does not close that residual. Worker-dependent behaviors
outside the editor were not exercised.

### Review follow-up — coverage and visual evidence (2026-09-21)

Based on `a9bcf57`, this follow-up closes the four remaining review findings.
The production editor is unchanged; changes are limited to tests,
documentation, and retained screenshots.

- T10 now exercises a boundary-bearing timer-catch target and every async
  automatic host target (`task`, `serviceTask`, `scriptTask`). It compares both
  timer and conditional boundary contracts and their flows before/after the
  transition. Boundary coordinates are excluded from contract comparisons
  because the existing render pass positions them around the new host shape.
- T12 now has two alternative ordinary starts in deliberately non-ID order
  (4 before 3), asserting that the first array-ordered start wins.
- E7 captures the populated workflow with host 7 selected immediately before
  and after service-task → user-task conversion at both required viewports.
  The assertions establish that the boundary exists before the first capture
  and that the boundary and its flow are absent before the second capture.
  Earlier `headed-*-before.png` files show startup only; use the retained
  populated comparisons below for the stage's visual baseline.
- The refactoring index now consistently marks Stage 10 implemented and only
  Stage 11 planned.

Validation against the follow-up:

- The [focused editor/parser command](#commands-and-acceptance-evidence)
  passed **321/321**, with no failures or skips.
- Three temporary mutations of the **build-copied** editor each produced the
  intended assertion failure: remove timer-catch host eligibility, remove
  async-script host eligibility, or choose the lowest-ID replacement start.
  The copied file was restored in a `finally` block and its SHA-256 matched the
  production editor. The targeted command
  `dotnet test Flowbit/tests/Flowbit.Tests/Flowbit.Tests.csproj --no-build --no-restore --filter FullyQualifiedName~TypeTransition_ --nologo --verbosity quiet`
  then passed **20/20**. Expected-failure TRX files are under
  `artifacts/browser/stage-10-mutation-results/`.
- With `FLOWBIT_BROWSER_HEADED=1`, the rebuilt browser command
  `dotnet test Flowbit/tests/Flowbit.BrowserTests/Flowbit.BrowserTests.csproj -c Release --filter "FullyQualifiedName~EditorSmokeTests|FullyQualifiedName~HarnessDiagnosticsTests" --nologo --verbosity quiet --logger "trx;LogFileName=stage-10-fixes.trx" --results-directory artifacts/browser/stage-10-fix-test-results`
  passed **20/20**, with no failures, skips, or retries. Headed Chromium
  **151.0.7922.34** served the copied editor at **http://127.0.0.1:52912/**.
  E1–E8 exercised file loading, saves/reloads, dragging, validation, keyboard
  focus, guarded changes, gateway Cancel/Accept, destructive conversion,
  undo/redo, and start/settings conversion at **1440×900 and 1024×768**.
  All 16 editor scenario diagnostics contain zero page errors, console errors,
  warnings, and unexpected dialogs. Harness cases intentionally inject errors.
  Raw evidence is under `artifacts/browser/runs/20260921-153850-475cd4d3/`.
- All four screenshots below were visually inspected: the fixture and selected
  host inspector are populated, the boundary/flow disappear after conversion,
  and no unexpected layout change appears. Links/image references and
  `git diff --check` passed. Native save-picker success/cancel remains the
  previously documented Stage 7 limitation; E7 uses the download fallback.

| Viewport | Before conversion | After conversion |
| --- | --- | --- |
| 1440×900 | [Selected service task and boundary](evidence/stage-10/boundary-1440x900-before.png) | [Selected user task, boundary removed](evidence/stage-10/boundary-1440x900-after.png) |
| 1024×768 | [Selected service task and boundary](evidence/stage-10/boundary-1024x768-before.png) | [Selected user task, boundary removed](evidence/stage-10/boundary-1024x768-after.png) |

### Documentation updated with this stage

- This plan (status, implementation record, checklist) plus
  [README](README.md), [roadmap](remaining-gaps-implementation-plan.md),
  [gap inventory](gaps.md), and the [documentation home](../index.md).
- [AGENTS.md](../../AGENTS.md) inspector ownership now names the transition
  helper and the retained redraw/history owners.
- The [browser suite README](../../Flowbit/tests/Flowbit.BrowserTests/README.md)
  (E6–E8 matrix, dialog queue support, new counts) and the
  [fixture catalog](../../Flowbit/tests/Flowbit.BrowserTests/Fixtures/README.md)
  (`editor-type-transition.json`).

No product documentation (node reference, BPMN support, developer guide,
runtime reference, root README) needed changes: the extraction preserves every
documented authoring behavior, JSON rule, and HTTP contract.

## Delivery and rollback

Keep characterization/harness changes and the application extraction reviewable
as separate buildable steps, with documentation completed in the same delivered
change. Avoid unrelated formatting churn in the large HTML file. Success is the
clear transition owner with preserved behavior, not a target line count.

Rollback is a revert of the helper extraction and selector adapter, restoring
the original inline callback, followed by the focused/full and browser checks.
Retain applicable characterization tests and reusable dialog-harness coverage.
There is no saved-model conversion, database migration, or runtime deployment
dependency. Restore documentation ownership/status if the extraction is reverted.
