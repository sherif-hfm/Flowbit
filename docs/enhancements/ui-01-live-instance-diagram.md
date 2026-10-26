# UI-01: Live instance diagram

[Enhancement backlog](README.md) · [Documentation home](../index.md)

| Field | Value |
| --- | --- |
| Status | Done |
| Category | Flowbit.Ui feature |
| Effort | M |
| Priority | Recommended order #1 |
| Depends on | None |
| Related | [UI-02](ui-02-process-analytics-heatmap.md) and [UI-07](ui-07-version-compare.md), which reuse this viewer; [UI-14](ui-14-instance-diagram-history.md), which adds the path; [EVENT-06](event-06-link-events.md) |

## Summary

Show the instance's exact workflow version as a read-only diagram on instance detail, highlighting where the instance is and what work is waiting. Selecting a node links to its execution detail. The path the instance took is [UI-14](ui-14-instance-diagram-history.md).

## Why it adds value

- Operators and end users see where an instance is at a glance. Today the instance page has only lists.

## First release acceptance criteria

The [plan](#plan) revised these criteria on 2026-10-26: finished instances mark their final positions, and node-execution links follow the caller's read access.

- [x] Instance detail shows the instance's exact workflow version as a read-only diagram, rendered by the shared editor assets.
- [x] On a running instance, nodes with active execution positions are highlighted and their waiting work is described, including multi-instance progress.
- [x] On a finished instance, the nodes where it ended, faulted, or was cancelled are marked.
- [x] Selecting a marked node lists its positions, with a link to the user task and links to the node executions the caller may read.
- [x] No historical path is drawn.
- [x] Verified in a real browser at desktop and phone widths, with no console errors.

## Later releases

This scope is recorded as [UI-14 Instance diagram history](ui-14-instance-diagram-history.md).

- Visited nodes from `node_executions`, with a notice when an instance's visits are incomplete: it started before that table's migration, or retention removed its visits.
- Flows taken, from the same rows: each visit's `EnteredViaFlowId` is one traversal. Multi-instance child visits all repeat their task's entry flow, so count it once per multi-instance execution.
- Markers for open incidents.

## Current state

Verified on 2026-10-26: done. The first release acceptance criteria are met.

- Instance detail renders the **Diagram** section ([`InstanceDiagram.razor`](../../Flowbit/src/Flowbit.Ui/Components/Shared/InstanceDiagram.razor)) after its summary. [`InstanceDiagramOverlay`](../../Flowbit/src/Flowbit.Ui/Components/Shared/InstanceDiagramOverlay.cs) derives the markers from instance detail.
- The shared editor's `viewer` mode in [flowbit-editor.js](../../Flowbit/src/Flowbit.Ui/wwwroot/editor/flowbit-editor.js) draws the diagram read-only, and [`workflow-editor-host.js`](../../Flowbit/src/Flowbit.Ui/wwwroot/workflow-editor-host.js) mounts it through `createViewer`.
- Node-execution links come from the role-scoped node-execution search. [Read the instance diagram](../ui-guide.md#read-the-instance-diagram) describes the markers, links, and navigation.
- Coverage: the Jint, unit, and component tests `EditorViewerModeTests`, `InstanceDiagramOverlayTests`, `InstanceDiagramComponentTests`, `InstanceDiagramPageTests`, and `WorkflowEditorHostViewerTests`, and [browser smoke scenario R8](../../Flowbit/tests/Flowbit.BrowserTests/README.md#what-is-covered) at 1440×900 and 390×844.
- `instance_history` is an audit trail, not a complete node-lifecycle ledger ([database schema](../database-schema.md#instance_history)); [UI-14](ui-14-instance-diagram-history.md) records why `node_executions` is the source for the path.

## Builds on

- `FlowbitEditor.mount(container, options)` and the shared rendering pipeline (`renderLanes`, `renderEdges`, `renderNodes`).
- Execution positions already returned by instance detail, and node execution detail pages for the links.

## Open questions

The plan decided each question; see [scope and decisions](#1-scope-and-decisions).

- A read-only option on the editor, or a separate viewer entry point in the same asset folder? AGENTS.md forbids duplicating editor markup, styles, or rendering in Razor. **Decided:** a `viewer` mode of `FlowbitEditor.mount`.
- The overlay data contract: can the existing instance detail response supply it, or is a new call needed? **Decided:** the instance detail response supplies the markers; node-execution links come from the existing node-execution search.
- Authorization: instance detail does not apply personal task visibility; may every viewer follow the links to node execution detail? **Decided:** no. Node-execution links appear only for executions the caller may read.

## Plan

Written on 2026-10-26, following [Writing a plan](README.md#writing-a-plan).

### 1. Scope and decisions

The first release adds a **Diagram** section to `/instances/{id}`. It draws the instance's current workflow version read-only and marks nodes using data that instance detail already returns. It changes no workflow JSON or execution behavior. A review on 2026-10-26 added one column and one response field so that a reactivated instance's last run stays identifiable after retention (sections 3 and 4).

- **Viewer entry point.** `FlowbitEditor.mount(container, { mode: "viewer" })` reuses the shared template, styles, and rendering pipeline. That pipeline lives inside the mount closure, so a separate entry point would have to duplicate it. Viewer mode hides the authoring controls, keeps the pan tool, and returns a smaller controller.
- **Overlay data.** Instance detail already returns the execution positions (node, token status, wait state, termination reason, user task, and multi-instance execution), multi-instance progress, completion, and fault. The page derives the node markers from them; no new endpoint is needed.
- **Authorization.** Any authenticated caller can read instance detail, but node-execution detail is role-scoped. When a node is selected, the page asks the existing `POST /api/node-executions/search` for that instance and node. The search returns only the executions the caller may read, and an empty page otherwise, so a node-execution link appears only when it would open. Task links appear as they do in the execution-position list today; the task page applies its own visibility.
- **Finished instances.** The diagram marks the final positions: where the instance ended, terminated, or faulted, and the work that was cancelled with it. After a reactivation, only the last run's positions count; the instance records where that run began. These are points, not a path.

Not in the first release: visited nodes, flows taken, and incident markers (see [Later releases](#later-releases)); editing; and diagram search or trace modes in the viewer.

### 2. Workflow JSON and model

No change. The viewer loads the definition through the editor's existing `normalizeLoadedModel()`, so older documents look the same as in the editor. `WorkflowModelMigrator` is unchanged.

### 3. Engine, persistence, and migrations

One additive column, added during review. Reactivation creates a fresh root token, and every later token of that run has a larger id. The run boundary was known only from the `instanceReactivated` history row, which history retention deletes while it keeps tokens. Reactivation therefore also stores the new root token in `workflow_instances.LastReactivationTokenId`, in its existing transaction. The `AddInstanceLastReactivationToken` migration fills the column from the reactivation history still present; a boundary whose history was already pruned cannot be recovered. There is no lock-order, Worker, or version-switch impact.

### 4. API contracts and authorization

One additive field, `lastReactivationTokenId` on instance detail. The UI reuses:

- `GET /api/instances/{id}` for the definition and the markers. Message credentials and the task-distribution secret are already redacted there, and any authenticated caller can already read the same definition through `GET /api/workflows/{id}`.
- `POST /api/node-executions/search` with `instanceId`, `nodeId`, `sort` `id desc`, and `pageSize` 50, and no status filter: an async-after wait follows a visit that has already completed. The page sends it only after a selection, and again on each refresh while a node stays selected.

### 5. Editor and Flowbit.Ui changes

Editor assets in `Flowbit/src/Flowbit.Ui/wwwroot/editor/`:

- **Viewer mode** in `flowbit-editor.js`:
  - light appearance and the pan tool only;
  - no inspector, history, connection handles, lane resize handles, focusable flows, trace highlighting, or editing shortcuts;
  - Escape clears the selection, and the mouse wheel zooms only with Ctrl or Meta so the page can scroll;
  - **Fit** stops at 100%.
- **Overlay.** `setOverlay({ nodes: [{ id, state, badge, label }] })` accepts the states `active`, `ended`, `faulted`, and `cancelled` and ignores invalid entries. A marked node gets:
  - a ring in its state's color, drawn around the shape so the node keeps its type colors;
  - an optional badge: `2/3` for multi-instance progress, `×2` for several tokens;
  - its label in the tooltip and accessible name;
  - keyboard focus.
- **Selection.** Clicking a node, or pressing Enter or Space on a marked node, selects it and calls `onSelectionChange(nodeId | null)`. A selected marked node has `aria-current`. When a marker update removes the focused node's marker, focus moves to the diagram.
- **Initial view.** It fits the whole diagram at no more than 100%. When that is too small to read (below 60%, where node captions hide) or leaves marked nodes outside the canvas, it centers the marked nodes at the smallest readable zoom instead. Marked nodes too far apart to read together give way to the leftmost of them, at that zoom.
- **Controller.** The viewer controller exposes `load(definition, overlay)`, `setOverlay(overlay)`, `selectNode(nodeId | null)`, and `dispose()`.
- **Page markup.** After initialization the viewer removes the toolbar (including the editor's own heading), authoring palette, diagram tools, inspector, and hint from the page; the dialogs stay hidden.
- **Styles.** `flowbit-editor.css` hides the same controls in viewer mode before they are removed, and styles the state rings, badge, selection ring, and focus outline.
- **Adapter.** `workflow-editor-host.js` gains a `createViewer` export that loads the same assets and adapts the viewer to Blazor.

Flowbit.Ui:

- `InstanceDiagramOverlay`, a pure helper, derives the markers from `InstanceDetailDto`:
  - for a running instance, its active positions grouped by node, described as user work, multi-instance progress, or a message, timer, condition, background-job, or gateway wait. Several multi-instance executions at one node are counted, and their progress is added up;
  - for a finished instance, the tokens of its last run (from `lastReactivationTokenId` onward, or the newest `instanceReactivated` history entry's token when the field is absent) that completed at normal or terminate ends, faulted, or were cancelled with the instance or by a terminate or error end. Tokens finished before termination reasons were recorded, on 2026-07-24, count by their status alone. Tokens cancelled by a scoped interrupt are not marked, and merged tokens are not returned;
  - for a legacy instance without positions, its current node.
- `InstanceDiagram.razor` hosts the viewer in an element it leaves empty and shows a legend. For the selected node it lists the positions with task links, multi-instance progress, and the node-execution links that the page supplies. It injects only `IJSRuntime` and owns only the viewer's lifecycle. A failed load or marker update is shown once and retried only after the instance changes.
- `InstanceDetail.razor` places the section after the summary and keeps the selection. It fetches node executions for the selected node with stale-response and identity guards, and treats a failed lookup as having no links.
- **Layout.** The details panel sits beside the canvas at 1,200 px and wider, and below it otherwise. The canvas is shorter on narrow screens.

### 6. Documentation

- [Flowbit.Ui guide](../ui-guide.md#inspect-instances-and-activity): the Diagram section, its legend, selection, link access, navigation, and what it does not show.
- [AGENTS.md](../../AGENTS.md): viewer mode, its controller, the adapter, the instance-detail component, and the browser coverage matrix.
- [Runtime reference](../../Flowbit/README.md), the [browser smoke suite](../../Flowbit/tests/Flowbit.BrowserTests/README.md), and the [root README](../../README.md) capability summary.
- This page and the [backlog](README.md): status, criteria, and a new item for the later-release scope.
- The [API reference](../api-guide.md) needs no change.

### 7. Tests and verification

- **Jint** (`Flowbit.Tests`): viewer-mode tool lock, inert editing shortcuts, overlay validation and rendering, the selection callback, wheel behavior, the readable opening view for marked nodes far apart, and the absence of trace highlighting, inspector content, and editing affordances.
- **Unit**: `InstanceDiagramOverlay` for parallel, same-node, multi-instance (one or several executions), every wait kind, completed, terminated, faulted, cancelled, reactivated (with and without history), and legacy instances, including tokens without termination reasons.
- **Component** (`HtmlRenderer`): the legend; the selected-node panel with task and node-execution links, including the completed visit an async-after wait follows; no node-execution links without visible executions; capped multi-instance links, with an "at least" count when the lookup was cut short; and instance detail rendering the section without a node-execution request. A test renderer that completes each render checks that a viewer which fails to mount or load shows its error once and retries after the instance changes.
- **Page state**: the selection lookup without a status filter, a failed lookup, and an answer that arrives after the selection changed.
- **PostgreSQL**: reactivation records its root token, which instance detail still returns after the history is removed; the migration fills the column from reactivation history.
- **Browser smoke R8** (`Flowbit.BrowserTests`) at 1440×900 and 390×844 with the existing gateway, multi-instance, and navigation fixtures: markers and badges, a final position, links for an administrator and their absence for a reviewer, following a node-execution link, a read-only drag and Delete, no highlighted flows, no horizontal overflow, and screenshots.
- [Real-browser verification](../../AGENTS.md#mandatory-real-browser-ui-verification) on a local stack at desktop and phone widths, with the console checked.
