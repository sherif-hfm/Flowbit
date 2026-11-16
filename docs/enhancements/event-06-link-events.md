# EVENT-06: Link events

[Enhancement backlog](README.md) · [Documentation home](../index.md)

| Field | Value |
| --- | --- |
| Status | Done |
| Category | New events |
| Effort | M |
| Priority | Not ranked |
| Depends on | None |
| Related | [UI-01](ui-01-live-instance-diagram.md), [UI-14](ui-14-instance-diagram-history.md) |

## Summary

Intermediate link throw and catch events, paired by name, that act as "go to" connectors in large diagrams. A token that enters a throw event continues at the catch event with the same name, as if a sequence flow joined the two events.

## Why it adds value

- Large diagrams, especially AI-generated ones, become easier to read without long crossing edges.
- A rework loop or a shared closing section can be entered from several places without drawing every return edge.

## First release acceptance criteria

These criteria are revised by this plan on 2026-11-15. The revision adds the rule that a catch needs a throw, the graph analyses that must follow a link, and the read-only instance diagram.

- [x] `intermediateLinkThrowEvent` and `intermediateLinkCatchEvent` pair by name within one workflow definition. Names are compared exactly after trimming, several throws may share one catch, and no JSON property is added.
- [x] The API and the editor report these problems with the same wording: a throw without an incoming flow or with an outgoing flow; a catch with an incoming flow or without exactly one unconditional outgoing flow; catch names that are equal ignoring letter case; a throw without a catch of its name; and a catch without a throw. The API also rejects task, role, variable, event, async, and multi-instance settings on either event.
- [x] A token that enters a throw continues from its catch in the same transaction and on the same gateway branch, without the Worker. Instance history and node activity record both events.
- [x] Every analysis that reads the workflow graph follows a link as it follows a sequence flow: inclusive and complex merge enabling, join-cancellation and scoped-interrupt reachability, reactivation safety, the shared-variable lock-order proof, version-change topology, and the AI assistant's reachability check.
- [x] The editor draws both shapes, refuses connections the rules forbid, shows each event's partners in the inspector with navigation to them, and highlights the paired events when either one is selected. The read-only instance diagram highlights and links them too.
- [x] Existing workflow JSON, runtime rows, and HTTP contracts keep their behavior, and no database migration is needed.
- [x] Documentation, an example, the portable authoring package, automated tests, and real-browser verification are complete before this item is marked Done.

## Later releases

- Renaming a link on its catch and all its throws in one step, and creating the missing partner from the inspector.
- A separate link key, so that a throw's caption can differ from its catch's.
- Drawing a connector between paired events while one of them is selected.
- Suggesting or inserting link pairs for long edges, in the editor or in the AI assistant's layout.

## Current state

Verified on 2026-11-15 and reviewed on 2026-11-16: done. The first release acceptance criteria are met.

- **Model and rules.** `BpmnFlowNodeTypes` in [WorkflowModel.cs](../../Flowbit/src/Flowbit.Shared/Models/WorkflowModel.cs) adds both types, `IsLinkThrow`, `IsLinkCatch`, and `IsLinkEvent`, and puts both in `IsPassThrough` and `IsSupported`. [LinkEventRules.cs](../../Flowbit/src/Flowbit.Shared/Models/LinkEventRules.cs) owns the name key, `LinkEventIndex` (the catch of each throw and the throws of each catch), `ImplicitEdges`, and `FindProblems`, which lists every topology and pairing problem in node order. `WorkflowDefinitionValidator` rejects the first of them before it builds the structural adjacency, rejects unsupported settings on authored input, and checks a catch's outgoing flow; `WorkflowModelMigrator` clears settings on link events and never rewrites names.
- **Runtime.** `ResolvePassThroughAsync` hops a throw to its catch through `HopLinkThrowAsync`: a `linkThrow` history row with no flow or action, the throw's visit completed as `normal` with no exit flow, and the catch's visit started with no entry flow on the same token and gateway branch. The catch then routes as a pass-through node with note `linkCatch`, which is also its flow's traversal kind. `IsActorActionHistory` lists both notes. No migration, job kind, route, or completion reason was added.
- **Graph analyses.** `BuildStructuralAdjacency`, `GatewayTopologyIndex` (which also gives routing `LinkCatchOf`), `FindUnsafeReactivationTopology`, the lock-order proof's `RouteChoices`, `ValidateExactTopology` (new blocker `link_event_contract_changed`), and `WorkflowAiProposalChecks.Reachable` all add the `LinkEventRules` edges. Diagram history and the assistant's layout needed no change.
- **Editor.** Both types, their shapes and icons (`icon-link-throw`, `icon-link-catch`) in both themes, refused connections, the type-change guard, invariants, `linkEventIndex` inside the validator markers, `validateSaveLinkEvents` with the server's messages, link edges in both structural adjacencies and in the trace walks (the navigation block receives them as data), the `link-partner` ring in authoring and viewer modes, partner titles, and the inspector's **Link** section with **Continues at**, live status lines, **Go to catch**, and the catch's throw buttons.
- **Flowbit.Ui.** `ExecutionPositionText` names both types, `InstanceHistory.razor` describes both notes, and `InstanceDiagram.razor` offers a selected link event's partners as buttons that select them through the viewer, paired by `LinkEventRules`.
- **Documentation and package.** The node reference, BPMN support, developer, UI, API, database schema, deployment, and AI authoring guides, the root and runtime READMEs, AGENTS.md, the example catalog, the portable authoring guide, and the package's capability list describe link events. [basics/11-link-events.json](../../examples/basics/11-link-events.json) is the new example; the catalog has 69 workflows and 24 node types.
- **Tests.** 135 new cases: `LinkEventRulesTests` (the shared fixtures, every message through the API, unsupported settings, normalization), `LinkEventEquivalenceTests` (join cancellation, scoped interrupts inside and outside a scope, the lock-order proof both ways, a reactivation hazard, merge reachability, and AI reachability, each compared with the flow equivalent), `LinkEventRuntimeTests` on PostgreSQL (history, visits, flow evidence, diagram history, the `nodeType` filter, two throws per catch, a parallel branch to its join, an inclusive merge waiting through a link, a loop, a link-only cycle, and arrival from a user action, a message, a timer boundary, an administrative action, an `asyncAfter` continuation, a finished call, and a multi-instance outcome), `EditorLinkEventTests` (the same fixtures through the editor validator, index parity, connections, type changes, invariants, trace walks, the ring in both modes, the inspector and its edge states, and the loader), and one case each for the version-change rule, the layout, the diagram panel, and the history text. Pinned counts moved to 24 node types, 69 examples, and 86 assistant files.
- **Full suite.** `dotnet test` of Flowbit.Tests ran 5,324 cases twice. Both runs failed only `LinkEventRuntimeTests.UserAction_HopsToTheCatchAndRecordsBothEventsHistoryAndFlowEvidence`: `NodeExecutionApiTests` leaves the shared `NodeExecution.RequiredRole` set to its own reader role, so a later `admin` search found nothing. The test now gets visibility from its workflow's `taskAssignmentRoles` and passes with `NodeExecutionApiTests` run first. After that change, a third full run of `dotnet test Flowbit/Flowbit.slnx` passed all 5,324 cases in 18 minutes.
- **Browser smoke.** The full suite passed all 133 cases in 13 minutes, including E18 with both link events in the census, the new E22 at 1440×900 and 1024×768, and the new R15 at 1440×900, 1024×768, and 390×844. A first E22 run failed only because **Go to catch** zoomed in and left the catch outside the view; the scenario now fits the diagram before its connection checks.
- **Real-browser verification.** On a local published stack in the built-in Chromium browser, a hosted workflow with three throws and one catch showed both shapes, the partner ring on the catch for a throw and on both exact-name throws for the catch, the case-only warning for a lowercase throw, and status lines that changed as the name was typed, with focus kept in the field. **Go to catch** selected the catch at 390×844 without horizontal overflow, the long names wrapped inside Bootstrap's styles at 1440×900, and the server accepted the save as version 1. The standalone editor drew both shapes and the partner rings in the light and dark themes at 1024×768. The console had no errors.
- **Review after completion (2026-11-16).** Two independent reviews, one of the server code and one of the editor and Flowbit.Ui, and a recheck of this plan's own lists against the docs, example, package, and tests.
  - **Server.** No defect was found in the rules, validation, routing, or graph analyses. One line was removed: the `linkCatch` arm added to `SequenceFlowTraversalKind`, a helper that has no callers. The traversal kind comes from the history note, and the PostgreSQL runtime test pins it.
  - **Editor fixes.** A partner button and the **Continues at** list are redrawn when used, which left focus on the page body, where the hosted editor ignores shortcuts. Focus now moves to the partner's inspector heading, as it does for an error boundary opened from its host, and returns to the new list after a choice. A catch without a name can no longer be chosen, which erased the throw's name. Picking the placeholder keeps the list on the current catch. When catches share a name, the throw's section says so, and the later catch says that its name's throws continue at the first one, as save validation counts them, instead of "no throw uses the name". The status lines live in one `role="status"` region. Typing a link event's name refreshes the trace as well as the ring. Partner buttons' accessible names contain their visible text.
  - **Flowbit.Ui fixes.** The instance diagram's partner buttons have accessible names that contain their visible text. In the viewer the partner ring is dotted and in the accent color, because the dashed ring sat two pixels inside the dashed grey **Reached** halo and read as the same mark.
  - **Manual checks added.** The standalone editor at 1440×900 in both themes: both shapes, the ring, the tab order through the **Link** section, Enter on **Go to catch**, and dragging a throw into another lane with undo. The hosted editor at 1024×768: the ring and the **Link** section inside Bootstrap's styles, the save, focus on the partner's heading, and Ctrl+Z from that heading. Instance detail at 1024×768 by hand: reached marks and flow counts for the rejection path, selecting the throw on the canvas after panning by drag, and the partner button bringing the catch into view at the same zoom with focus kept in the panel. The edge states above with real typing in the dark theme. The console had no errors.
  - **Evidence.** After these changes `dotnet test Flowbit/Flowbit.slnx` passed all 5,325 cases in 18 minutes. The browser suite then ran its 133 cases and 131 passed, including E18, E22 with the new focus assertions, and R15 with the ring assertions at all three viewports. `LocalAuthenticationSmokeTests` at 1440×900 timed out waiting for the page to turn interactive and passed when rerun alone. `InstanceDiagramFitBrowserTests` (`canonical`) failed, and fails the same way with the committed editor files in place of the changed ones: the Windows session of this run had font smoothing off, which widens the measured captions (initial view X −30.5 instead of −25.97) and moves Y 0.19 past the test's tolerance. The run of 2026-11-15, with font smoothing on, passed it.
  - **Left unchanged.** Four observations are not caused by link events and were not changed: the editor's blank test for a flow condition uses JavaScript `trim()`, so a condition of only U+0085 counts as text there and as blank on the server; **Add flow** on a catch, as on a start event, first targets the node itself; the lock-order proof queues the continuation of a scoped interrupt one step later than the engine does when a pass-through node precedes it; and `GatewayTopologyCache` is keyed by definition id alone, which two test databases in one process can share. The engine's error for a throw without a catch has no test, because only a directly inserted definition can reach it.

## Builds on

- [WorkflowModel.cs](../../Flowbit/src/Flowbit.Shared/Models/WorkflowModel.cs), [WorkflowModelMigrator.cs](../../Flowbit/src/Flowbit.Shared/Models/WorkflowModelMigrator.cs), and [WorkflowDefinitionValidator.cs](../../Flowbit/src/Flowbit.Service/Services/WorkflowDefinitionValidator.cs): node types, per-type normalization, topology rules, and `BuildStructuralAdjacency`, which already adds boundary attachments as edges.
- [WorkflowEngineService.cs](../../Flowbit/src/Flowbit.Service/Services/WorkflowEngineService.cs): `ResolvePassThroughAsync`, `AdvanceAutomaticTokenAsync`, and the scoped interrupt as the model of a pass-through control event.
- [GatewayTopologyCache.cs](../../Flowbit/src/Flowbit.Service/Services/GatewayTopologyCache.cs), [WorkflowEngineService.Reactivation.cs](../../Flowbit/src/Flowbit.Service/Services/WorkflowEngineService.Reactivation.cs), [SharedVariableTransactionLockOrderValidator.cs](../../Flowbit/src/Flowbit.Service/Services/SharedVariableTransactionLockOrderValidator.cs), [WorkflowVersionCompatibilityEvaluator.cs](../../Flowbit/src/Flowbit.Service/Services/WorkflowVersionCompatibilityEvaluator.cs), and [WorkflowAiProposalChecks.cs](../../Flowbit/src/Flowbit.Service/Ai/WorkflowAiProposalChecks.cs): the graph analyses.
- [InstanceDiagramHistoryQueryService.cs](../../Flowbit/src/Flowbit.Service/Services/InstanceDiagramHistoryQueryService.cs) and [WorkflowAuthoringLayout.cs](../../Flowbit/src/Flowbit.Service/Authoring/WorkflowAuthoringLayout.cs): analyses that need no change, each pinned by a test.
- [Canonical editor](../../Flowbit/src/Flowbit.Ui/wwwroot/editor/flowbit-editor.js), its [template](../../Flowbit/src/Flowbit.Ui/wwwroot/editor/editor-template.js), and its [styles](../../Flowbit/src/Flowbit.Ui/wwwroot/editor/flowbit-editor.css): the node registry, event drawing, connection rules, the trace context that highlights related nodes, the save validator with `trimAttributeKey` and `ordinalIgnoreCaseAttributeKey`, and viewer mode.
- [Portable authoring guide](../../authoring/flowbit-authoring/references/authoring-guide.md) and the [browser smoke suite](../../Flowbit/tests/Flowbit.BrowserTests/README.md).

## Open questions

No question remains open. The plan decides the original question and the ones it raised:

- **Virtual flow or recorded events?** Record both events as node visits and history rows, and add no virtual sequence flow. A virtual flow would need an id that flow evidence, `FlowInfo`, history, and diagram history could refer to, and the diagram would then show a path that skips two drawn events.
- **What is the link name?** The event's own `name`. The caption a reader sees is the pairing key, so a printed diagram can be followed, and the JSON gains no property. A separate key is left to [Later releases](#later-releases).
- **How are names compared?** Exactly, after trimming, with no Unicode normalization. Catch names must be unique ignoring letter case, so two links cannot differ only in case, and a throw that matches a catch only ignoring case is told so.
- **Must a catch have a throw?** Yes. An unused catch is always a leftover or a misspelled name, and the AI assistant's check would report it as unreachable anyway.
- **One step or an instant jump?** Each event is one ordinary routing step. A link then behaves exactly like two automatic pass-through nodes joined by a sequence flow, and the lock-order proof can mirror it with one routing rule. Resolving the jump at the moment a token arrives would need a second code path in every place that moves a token, and the proof would have to model both.
- **New completion reasons?** No. Both visits complete as `normal`, so the reason check constraint and its migration stay untouched. Two new history notes tell the steps apart.

## Plan

Written on 2026-11-15 following [Writing a plan](README.md#writing-a-plan). Effort remains **M**: the runtime change is small and needs no migration, but the link edge has to reach every graph analysis in the service and the editor, and both validators must agree.

### 1. Scope and non-goals

Add two node types, `intermediateLinkThrowEvent` and `intermediateLinkCatchEvent`. A link connects two places in one workflow definition; a call activity remains the way to reach another workflow.

The runtime contract:

- When a token is on a throw, the engine moves it to the catch with the same name, in the same transaction and on the same gateway branch. Routing then follows the catch's one outgoing flow.
- Both events are pass-through nodes. Neither rests, waits, reads or writes variables, or needs the Worker.
- Several throws may share one catch. A catch may come before its throws in the flow, which makes a loop.
- An instance cannot start at a catch, and a catch is never a reactivation target.

This release adds no JSON property, database migration, job kind, HTTP route, setting, or connector line between the paired events. It does not insert link pairs automatically, and it does not import or export BPMN XML.

### 2. Workflow JSON and validation

These are fragments, not a complete workflow:

```json
{
  "flowNodes": [
    { "id": 7, "type": "intermediateLinkThrowEvent", "name": "Fulfilment", "laneId": 1, "x": 900, "y": 160 },
    { "id": 12, "type": "intermediateLinkCatchEvent", "name": "Fulfilment", "laneId": 2, "x": 80, "y": 420 }
  ],
  "sequenceFlows": [
    { "id": 601, "sourceRef": 6, "targetRef": 7 },
    { "id": 1201, "sourceRef": 12, "targetRef": 13 }
  ]
}
```

| Subject | Contract |
| --- | --- |
| Link name | The event's `name`, compared after trimming as .NET `Trim()` does. The comparison is exact and case-sensitive, with no Unicode normalization. |
| Catch names | Unique among link catch events under `StringComparer.OrdinalIgnoreCase`. Nodes of other types may share the name. |
| Throw | At least one incoming flow and no outgoing flow. Exactly one catch has its name. |
| Catch | No incoming flow, and exactly one outgoing flow that is unconditional and carries no user-action or multi-instance settings. At least one throw has its name. |
| Other settings | Only `id`, `name`, `externalId`, `attributes`, `laneId`, `x`, and `y`. A boundary event cannot attach to a link event, because the existing host rules list the host types. |

Proposed messages, the same in the API and the editor:

| Problem | Message |
| --- | --- |
| Throw without an incoming flow | `Link throw event #7 must have at least one incoming sequence flow.` |
| Throw with an outgoing flow | `Link throw event #7 cannot have outgoing sequence flows.` |
| Catch with an incoming flow | `Link catch event #12 cannot have incoming sequence flows.` |
| Catch without exactly one outgoing flow | `Link catch event #12 must have exactly one outgoing sequence flow.` |
| Catch flow with a condition or action settings | `Link catch event #12 must have one unconditional outgoing sequence flow without user-action or multi-instance metadata.` |
| Two catches with one name | `Link catch event #15 has the same name as link catch event #12 ('Fulfilment'); link catch names must be unique, ignoring letter case.` |
| Throw without a catch | `Link throw event #7 has no link catch event named 'Fulfilment'.` |
| Throw that matches only ignoring case | `Link throw event #7 is named 'fulfilment', but link catch event #12 is named 'Fulfilment'; link names must match exactly, including letter case.` |
| Catch without a throw | `Link catch event #12 has no link throw event named 'Fulfilment'.` |
| Unsupported settings, API only | `Link throw event #7 cannot define task, role, variable, event, or multi-instance metadata.` |

Each message starts with `<noun> #<id>`, which `WorkflowAiDiagnostics.DefinitionPointer` turns into the JSON pointer returned to the AI assistant.

Model and service changes:

- **`BpmnFlowNodeTypes`.** Add the two constants and `IsLinkThrow`, `IsLinkCatch`, and `IsLinkEvent`, and add both types to `IsPassThrough` and `IsSupported`. The authoring schema's type list reads the constants by reflection.
- **`LinkEventRules`**, a new dependency-free class in `Flowbit.Shared/Models`. It owns the name key, the catch of each throw, the throws of each catch, the problems above, and `ImplicitEdges(definition)`. Every analysis in [section 3](#3-engine-and-graph-analyses) reads the pairing from it, so none repeats the name comparison.
- **`WorkflowDefinitionValidator`.**
  - `ValidateAuthored` rejects unsupported node settings on a link event, as it does for a scoped interrupt, and action or routing settings on a catch's outgoing flow.
  - `ValidateNormalized` checks topology and pairing before it calls `BuildStructuralAdjacency`, so the reachability rules always see resolved pairs. A blank name keeps the existing name-required message.
  - The generic one-outgoing-flow rule names the catch, and so does the engine's label chain in `SelectPassThroughFlow`.
- **`WorkflowModelMigrator`.** `ApplyNodeInvariants` clears task, role, variable, script, message, and attachment settings on link events, in the branch that already does so for scoped interrupts. Names are not rewritten. Older documents contain neither type and normalize as before.

A loop that contains only link events and other automatic nodes is not rejected when saved. It fails at run time with the existing routing-cycle error, as any automatic cycle does.

### 3. Engine and graph analyses

#### Routing

`ResolvePassThroughAsync` gets one branch for a throw, beside the gateway branches and before `SelectPassThroughFlow`, which would otherwise refuse a node with no outgoing flow. The branch:

1. Finds the catch through the cached `GatewayTopologyIndex`. A missing catch throws a `WorkflowDomainException`; validation prevents it, but directly inserted test definitions can reach it.
2. Writes a history row from the throw to the catch.
3. Calls `UpdateExecutionTokenAsync` with the catch's snapshot, the token's current gateway branch, and no arriving flow, completing the throw's visit.
4. Queues the token.

The catch then takes the generic pass-through path with its own history note. `AdvanceAutomaticTokenAsync` needs no change: it places a token on a throw as on any other node.

| Step | History row | Node activity |
| --- | --- | --- |
| A flow reaches the throw | The source node's usual row | The throw's visit starts, entered through that flow. |
| Throw to catch | Note `linkThrow`, from the throw to the catch, no action id | The throw's visit completes as `normal` with no exit flow. The catch's visit starts with no entry flow. |
| Catch to its target | Note `linkCatch` | The catch's visit completes as `normal`, exited through its flow. |

- `InstanceHistoryNotes` gains `LinkThrow` and `LinkCatch`, and `IsActorActionHistory` lists both among the automatic notes.
- The hop itself has no flow evidence, because it has no flow. The flow into the throw and the flow out of the catch record traversals as usual; the second has kind `linkCatch`. `FlowInfo` cannot refer to the hop.
- Each event counts as one hop against the per-token and routing-step limits. The automatic-activation guard is unaffected, because it counts only task, service, script, and call activities.
- A link takes two routing steps, like two automatic tasks. A scoped interrupt placed after a link therefore takes effect two steps later than one reached directly from a fork. The node reference states this.

#### Graph analyses

Each analysis below reads sequence flows as the graph. The change adds the throw-to-catch edge from `LinkEventRules`.

| Analysis | Code | Change |
| --- | --- | --- |
| `joinCancellation` and `scopedInterruptEvent.gatewayRef` reachability | `WorkflowDefinitionValidator.BuildStructuralAdjacency` | Add each throw-to-catch edge. |
| Inclusive and complex merge enabling, complex drains | `GatewayTopologyIndex` | Add each throw as a predecessor of its catch, and expose the catch of a throw for routing. |
| Reactivation hazards | `FindUnsafeReactivationTopology` | Add the edges to its node adjacency, so a target that reaches a scoped interrupt or synchronizing merge through a link is refused. |
| Shared-variable lock order | `SharedVariableTransactionLockOrderValidator.RouteChoices` | A throw routes to its catch. The catch routes through its flow as any pass-through node. Link events acquire no keys. |
| Version-change topology | `WorkflowVersionCompatibilityEvaluator.ValidateExactTopology` | Block when a throw resolves to a different catch; see [section 4](#4-persistence-versions-and-deployment). |
| AI assistant reachability | `WorkflowAiProposalChecks.Reachable` | Add the edges. Without them a catch and everything after it is reported as unreachable. |
| Diagram history | `InstanceDiagramHistoryQueryService` | None. Visits mark both events as reached, and the catch's flow is counted on exit. |
| Assistant layout | `WorkflowAuthoringLayout` | None, on purpose. A catch has no incoming flow, so it starts a row in column 0 of its lane and the section after it reads left to right. |

These analyses already differ in the boundary attachments they follow. `GatewayTopologyIndex` follows error boundaries only, and the reactivation check and the lock-order proof have their own models. This item adds link edges to each and leaves boundary handling as it is.

The lock-order proof simulates the engine's queue, so its routing must match the engine step for step. The engine queues the token after the hop from throw to catch, and the proof appends the catch in the same way.

### 4. Persistence, versions, and deployment

- **Schema.** No migration. The node type columns are `varchar(64)` and both names have 26 characters. History notes are unconstrained, and `sequence_flow_occurrences."Kind"` allows 32 characters. Completion reasons do not change.
- **Version changes.** A token never rests on a link event when a transaction commits, so adding, removing, or renaming link events does not block a version change by itself. When exact topology is required, because several tokens or gateway state are active, `ValidateExactTopology` already blocks added and removed nodes and flows. It gains one rule: a throw that resolves to a different catch node blocks with the new code `link_event_contract_changed`. Renaming a link on all its events keeps every pair and is allowed.
- **Reactivation.** The target rules do not change. Only the hazard search follows links.
- **Retention, webhooks, and jobs.** No change. Link events create no job, wait, or lifecycle event.
- **Deployment order.** Upgrade every API, UI, and Worker replica before anyone saves a definition that contains a link event. A replica without this release treats a throw as a resting node: it leaves the token there with no wait that could resume it, and the instance has to be cancelled. There is no publication switch for link events, so this order is the guard. Rolling the binaries back needs the same care: first retire every definition that contains link events and finish or cancel its instances.

### 5. API contracts and authorization

No route, DTO, or authorization rule is added or changed.

- Definition create, update, and read-only validation return the messages in [section 2](#2-workflow-json-and-validation) through the existing error shapes.
- Existing responses that carry a node type, such as execution positions, node executions, and jobs, can carry the two new values.
- `NodeExecutionQueryService` accepts both types in its `nodeType` filter. The `completionReason` filter is unchanged.
- Instance history can contain the notes `linkThrow` and `linkCatch`.
- `GET /api/instances/{id}/diagram-history` reports both events as reached and counts the flows on either side of the link.

### 6. Editor

All changes are in the canonical assets under `Flowbit/src/Flowbit.Ui/wwwroot/editor/`.

#### Types and shapes

- Add both types to `NODE_TYPE`, `NODE_TYPE_OPTIONS` ("Link Throw Event (go to)" and "Link Catch Event (continue here)"), `typeLabel`, and the type names inside `searchDiagram`.
- Add `isLinkThrowEventType`, `isLinkCatchEventType`, and `isLinkEventType`. Both types join `isEventType`. The catch joins `isSingleOutgoingType` and `isPassThroughType`, so its outgoing edge is dashed like other automatic flows.
- Both are `EVENT_D` circles with the inner ring of an intermediate event. `editor-template.js` gains `icon-link-throw`, a filled arrow, and `icon-link-catch`, the same arrow outlined, following the error throw and catch pair. `flowbit-editor.css` gains body, ring, selected, and icon rules in both themes. Shape sizes do not change, so the portable guide's size sentence stays correct.
- The node's hover title adds the partner, for example `Continues at #12`.

#### Connections and type changes

- `connectionSourceProblem` refuses a throw as a source: `A link throw event cannot have an outgoing sequence flow.` `connectionTargetProblem` refuses a catch as a target: `A link catch event cannot have an incoming sequence flow.`
- The inspector hides the outgoing-flows section for a throw, as it does for an end event.
- `changeNodeTypeFromInspector` rejects a change to a throw while the node has outgoing flows, with the alert used for end events reworded for a throw. A change to a catch keeps the first outgoing flow, as other single-outgoing types do, and leaves incoming flows for save validation to report, as a change to a start event does.
- `applyTypeInvariants` clears unsupported fields on link events in a branch like the scoped interrupt's, and `applyFlowInvariants` clears routing settings on a catch's flow. The two test harnesses that extract `applyTypeInvariants` stub every predicate by hand and need the new ones.

#### Pairing and highlight

- One helper, `linkEventIndex(candidate)`, inside the `// BEGIN/END WORKFLOW SAVE VALIDATOR` markers, is the editor's only pairing code. It uses `trimAttributeKey` and `ordinalIgnoreCaseAttributeKey`, which already match .NET. The navigation block runs alone in its Jint suite, so it receives the index as data and never calls the helper.
- `renderNodes` adds the class `link-partner`, a dashed ring in the selection color, to the partners of the selected link event: the catch for a throw, and every throw for a catch. It applies in authoring and viewer modes.
- `buildDiagramTraceContext` treats a pair as an edge in the upstream, downstream, and route walks, because execution crosses it. In local mode the partners become context nodes, so they are not muted.
- The structural adjacency in `createSaveValidationContext` and `buildGatewayAdjacencyIndex` gains the link edges. The join-cancellation and scoped-interrupt selectors, the forbidden-merge warning, and the save rules then agree with the server.

#### Inspector

A **Link** section appears for both types, with its explanation behind `attachInspectorHelp`.

- **Throw.** A **Continues at** selector lists the link catch events by name. Choosing one copies its name to the throw as one undoable change. A status line names the catch, or warns that no catch has this name, and **Go to catch** selects it and brings it into view through the path diagram search uses.
- **Catch.** The section lists the throws that use the name, each as a button that selects that throw, or says that no throw uses the name yet.
- The status lines update as the name is typed. They are state-dependent notes; the fixed explanation stays in the help panel. Every control has a label, so the E18 census passes.

#### Save validator and viewer

- A new phase, `validateSaveLinkEvents`, runs after `validateSaveEndEvents` and gives the messages in [section 2](#2-workflow-json-and-validation). The supported-type list in `validateSaveNodeTypes` gains both types. Documents without link events produce the same ordered errors as before.
- Viewer mode draws the shapes and the partner ring and adds no authoring control. History marks both events as reached, and no edge is drawn between them.

### 7. Flowbit.Ui

- `ExecutionPositionText.NodeTypeLabel` names both types.
- `InstanceHistory.razor` describes the `linkThrow` and `linkCatch` rows instead of showing the raw note.
- `InstanceDiagram.razor` shows a selected link event's partners as buttons in its node panel. Each calls the viewer's `selectNode`, which brings the partner into view without changing zoom. The pairs come from `LinkEventRules`, so the page and the engine agree.
- Activity filters take node types as free text and need no change.

### 8. AI authoring and the portable package

- The hosted assistant's instructions, tool names, and tool descriptions do not change. Its guides are the owning documents, so it reads about link events once [section 9](#9-documentation-and-examples) is done.
- [The portable authoring guide](../../authoring/flowbit-authoring/references/authoring-guide.md) gains a short paragraph: use a link for a long return or crossing flow, or for a section entered from several places; give the throw and the catch the same `name`; a catch has no incoming flow, so place it at the start of a row; do not replace short flows.
- The hand-written `capabilities` list in `AuthoringPackageBuilder` gains link events. The schema, capability index, and manifest are generated, and the package is exported again by the API build.
- The layout is unchanged. A test pins that a link example lays out without overlaps and that each catch starts its own row.

### 9. Documentation and examples

| Owning page | Update |
| --- | --- |
| [BPMN support](../bpmn-support.md) | The node count, two rows in the supported table, the graph rule for a throw without an outgoing flow and a catch without an incoming one, and the unsupported list without link events. |
| [Node reference](../node-reference.md) | The node count, two catalog rows, and a `### intermediateLinkThrowEvent` and a `### intermediateLinkCatchEvent` section in the existing template: pairing by name, comparison rules, topology, the two routing steps, and loops. A package test requires both headings. |
| [Developer guide](../developer-guide.md) | Recorded diagram history across a link, and the version-change rule. |
| [Flowbit.Ui guide](../ui-guide.md) | Authoring a link, the partner highlight, and reading a link in the instance diagram. |
| [API reference](../api-guide.md) | The new node type values and history notes where the vocabularies are described. No route changes. |
| [Database schema](../database-schema.md) | The example history notes. No schema change. |
| [Deployment](../deployment.md) | The upgrade order and the rollback condition in [section 4](#4-persistence-versions-and-deployment). |
| [AI authoring](../ai-authoring.md) and [root README](../../README.md) | The node count and the building-block table. |
| [Runtime reference](../../Flowbit/README.md) and [AGENTS.md](../../AGENTS.md) | Link routing, the history notes, `LinkEventRules` as the one source of pairing, the list of analyses that must follow a link, and the editor conventions. |
| [Example catalog](../../examples/README.md) | Add `examples/basics/11-link-events.json`: a review whose approval and rejection paths both end in throws, one catch that two throws share, no Worker, and no required start values beyond those it documents. Update the example count and the coverage matrix. Every node type must appear in an example, so this file uses both. |
| Browser suite READMEs | The new scenarios and the census fixture. |
| This page and the [backlog](README.md) | Evidence and status. |

Validate changed relative links, anchors, and the example JSON. No guide is added, so the [documentation home](../index.md) does not change.

### 10. Tests and verification

| Layer | Cases |
| --- | --- |
| Shared rules | `LinkEventRules`: trimming with the characters .NET trims, exact and case-only matches, duplicate catches, several throws per catch, and non-ASCII names. One fixture set feeds the C# rules and the editor's validator through Jint. |
| Definition validation | Every message in [section 2](#2-workflow-json-and-validation), unsupported settings on authored input, the one-outgoing rule, and older documents normalizing as before. |
| Equivalence | For each analysis in [section 3](#3-engine-and-graph-analyses), two definitions that differ only in a link pair replaced by two automatic tasks joined by a flow get the same answer: a join-cancellation merge and a scoped interrupt reachable through a link, a descending shared-key order across a link, a reactivation hazard behind a link, and an AI proposal whose catch is reachable. |
| PostgreSQL runtime | One throw; two throws sharing a catch; a link inside a parallel branch that ends at its join; an inclusive merge that waits for a token still able to reach it through a link; a loop through a link with a user task; a link-only cycle that fails with the routing-cycle error; and arrival on a throw from a user action, a message, a timer boundary, an administrative action, an `asyncAfter` continuation, a finished call, and a multi-instance outcome. |
| Records | History notes and order; two node visits with their entry and exit flows; flow evidence on both sides; diagram history marking both events and counting the catch's flow; the `nodeType` filter; an exact-topology version change that keeps, renames, and repoints a link. |
| Editor (Jint) | The shared fixtures and ordered errors; type conversion and its guard; invariants; connection problems; the pairing index; trace walks and context; the partner class in both modes; the inspector section with selection and navigation; loader round trip; the characterization arrays unchanged. |
| Pinned counts | 24 node types in `WorkflowAuthoringPackageTests`, 69 examples in `ExampleWorkflowCatalogTests`, and 86 files and 69 examples in `WorkflowAiWorkspaceTests`. |

Browser smoke suite, at 1440×900 and 1024×768:

- **E22, standalone editor.** Turn two nodes into a throw and a catch; pair them with **Continues at**; see the partner ring and use **Go to catch**; see a connection to a catch and from a throw refused; break the name and see the status line and the save dialog's message; undo and redo; save and reload.
- **R15, runtime.** Publish the link example over HTTP, complete it through the UI, and check on instance detail that recorded history marks both events, counts the flows on either side, and that selecting the throw rings and links the catch. Also run at 390×844.
- **E18.** Add both types to `editor-inspector-census.json` and extend the node id range the scenario selects.

Then follow [mandatory real-browser verification](../../AGENTS.md#mandatory-real-browser-ui-verification) by hand. Check the standalone editor and the hosted editor, where Bootstrap can restyle generic classes, in both themes at 1440×900 and 1024×768, the hosted inspector at 390×844, and the instance diagram. Use real clicks, typing, keyboard navigation, and drags. Check the console, and keep screenshots of both shapes, the partner ring, and the inspector section.

Regression commands, run from the repository root with the same arguments in PowerShell 7 and Bash:

```text
dotnet test Flowbit/Flowbit.slnx --nologo --verbosity quiet
dotnet publish Flowbit/src/Flowbit.Api/Flowbit.Api.csproj -c Release -o artifacts/browser/hosts/api /p:UseAppHost=false
dotnet publish Flowbit/src/Flowbit.Ui/Flowbit.Ui.csproj -c Release -o artifacts/browser/hosts/ui /p:UseAppHost=false
dotnet build Flowbit/tests/Flowbit.BrowserTests/Flowbit.BrowserTests.csproj -c Release
dotnet test Flowbit/tests/Flowbit.BrowserTests/Flowbit.BrowserTests.csproj -c Release --no-build --no-restore
git diff --check
```

The [suite README](../../Flowbit/tests/Flowbit.BrowserTests/README.md) covers Docker, the Chromium install, and evidence paths. Full solution runs have had order-dependent failures at the base commit ([EVENT-09](event-09-cron-schedules.md#current-state) records one), so compare failures against a base run before attributing one to this change. The Worker-enabled acceptance suite is not needed, because link events create no durable work.

### 11. Delivery sequence and risks

1. **Contract.** Node types, `LinkEventRules`, validator, migrator, and the shared fixtures. Exit with definition tests green and older documents unchanged.
2. **Engine and analyses.** The routing branch, history notes, and every row of the graph-analysis table, each with its equivalence test. Exit with the PostgreSQL runtime and record tests green.
3. **Editor.** Types, shapes, connection rules, the pairing index, the validator phase, the inspector section, the highlight, and viewer mode. Exit with the Jint suites green.
4. **Flowbit.Ui.** Labels, history text, and the diagram panel's partner buttons.
5. **Documentation and package.** The example, the owning guides, the authoring guide, and the pinned counts.
6. **Verification.** Both browser scenarios, the census, and the manual checks. Record the evidence under [Current state](#current-state), then set the status to Done.
7. **Deployment.** Upgrade every replica before a link event is authored.

| Risk | Guard |
| --- | --- |
| An analysis misses the link edge, so a definition validates or routes differently from its flow equivalent. | One pairing source, the analysis table, and an equivalence test per row. |
| The C# rules and the editor's validator drift. | Shared fixtures run on both sides, with the same messages. |
| A replica without this release strands a token on a throw. | The deployment order, stated in the deployment guide. |
| Renaming one event silently breaks a pair. | The inspector's live status, the missing partner ring, and the save rules for an unmatched throw and an unused catch. |
| The proof's routing differs from the engine's. | Both treat each link event as one queue step; a test compares a descending-key definition across a link with its flow equivalent. |
