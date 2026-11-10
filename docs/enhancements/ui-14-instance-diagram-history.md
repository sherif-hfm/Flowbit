# UI-14: Instance diagram history

[Enhancement backlog](README.md) · [Documentation home](../index.md)

| Field | Value |
| --- | --- |
| Status | Done |
| Category | Flowbit.Ui feature (with API and persistence changes) |
| Effort | M |
| Priority | Not ranked |
| Depends on | [UI-01](ui-01-live-instance-diagram.md) |
| Related | [UI-02](ui-02-process-analytics-heatmap.md), which can reuse the aggregation rules; [UI-05](ui-05-live-refresh.md), which can later supply refresh signals |

## Summary

Add recorded history to the instance diagram: nodes reached and sequence flows taken, alongside the current or final positions already provided by UI-01. The first release shows what was recorded under the instance's current exact workflow version, across all retained runs. It explains missing or uncertain history instead of presenting the diagram as a complete reconstruction. Anyone who can open the instance can read the history summary; execution detail stays limited to node-activity readers.

This implementation plan was prepared against the repository on **2026-11-09** and completed on **2026-11-10** after code review, PostgreSQL evidence characterization, and browser verification. The summary endpoint, coverage migrations, history controls, viewer overlays, and authorized execution paging are implemented. The validation record below distinguishes completed feature checks from outstanding deployment checks. Open-incident markers remain later-release work.

## Why it adds value

- Operators can follow an instance's recorded path without reading individual history rows.
- Loops and parallel routes become visible without hiding where work is waiting now.
- Coverage and version notices prevent an incomplete diagram from being mistaken for proof that a route was never taken.

## First release acceptance criteria

The plan revises the original criteria to define access, version/run scope, and multi-instance entries, which have no node-execution row of their own.

- [x] **Show recorded history** adds distinct markers for reached nodes and taken flows without changing current/final position markers or their badges.
- [x] The summary uses the same access check as instance detail ([decision](#summary-read-boundary)). Execution lists and links keep node-activity authorization: `NodeExecution.RequiredRole` or the `taskAssignmentRoles` of each execution's version.
- [x] PostgreSQL aggregates the full retained evidence for the displayed version, not the first page of executions. Each committed traversal counts once. Each multi-instance entry counts once, including an entry interrupted or cancelled before its work items were created. The one exception, an entry directly from an async-after node or another multi-instance task, has its own coverage reason.
- [x] Current/final positions retain UI-01's latest-run meaning. History covers all retained runs of the displayed exact definition, including the documented open-target rebinding exception; records still assigned to other versions are not drawn onto it.
- [x] Known cutover gaps, actual node-activity pruning, uncertain legacy coverage, unrecorded multi-instance entries, and unmappable evidence have clear notices. Empty, failed, and partially retained history, and restricted execution detail, are not conflated.
- [x] Historical-only nodes can be selected with the pointer on the canvas and from the keyboard through a textual history list, with bounded, authorized execution detail and textual flow counts.
- [x] Refreshes, actions, identity changes, version changes, navigation, and disposal cannot apply obsolete responses or expose data from a previous identity. Polling does not repeat the aggregation while the instance is unchanged.
- [x] Workflow JSON, runtime recording, execution outcomes, editor authoring, task permissions, and UI-01's history-off view remain compatible. The follow-up real-browser fit checks are recorded below.
- [x] Focused feature and regression checks pass, and real-browser verification covers desktop, tablet, and phone widths with screenshots and console evidence. Full-suite results and their limitations remain recorded below.

Completion scope was agreed on **2026-11-10**: the authentically aged retention walkthrough and production-copy migration timing are [deployment follow-ups](../deployment.md#instance-diagram-history-deployment-checks). They remain outstanding and are tracked separately from UI-14 feature acceptance.

## Later releases

- Open-incident markers, with their own authorization and lifecycle rules.
- Historical-version selection, including a precise policy for visits carried across a version switch.
- Individual-run selection, chronological playback, and time sliders.
- Recording multi-instance entries that come directly from an async-after node or another multi-instance task and end before work items exist, if the first release's coverage notice proves insufficient.
- Cross-instance analytics, duration metrics, and heatmaps in [UI-02](ui-02-process-analytics-heatmap.md).
- Signal-driven updates through [UI-05](ui-05-live-refresh.md).

## Baseline before implementation

Repository review on 2026-11-09, rechecked before implementation on 2026-11-10, established the following baseline. These points describe the earlier implementation; the current behavior and verification results are recorded below.

- [`InstanceDiagramOverlay`](../../Flowbit/src/Flowbit.Ui/Components/Shared/InstanceDiagramOverlay.cs) derives current/final markers from instance detail, without historical nodes or flow counts.
- [`InstanceDetail.razor`](../../Flowbit/src/Flowbit.Ui/Components/Pages/InstanceDetail.razor) requests the newest 50 node executions only after node selection, without a version filter. This provides authorized links, not complete path totals. While an instance has several active positions or waits on multi-instance work, a message, or a call, the page refreshes every 5 seconds.
- [`flowbit-editor.js`](../../Flowbit/src/Flowbit.Ui/wwwroot/editor/flowbit-editor.js) accepts node-state overlays in viewer mode and disables authoring trace highlighting. Overlay updates redraw nodes, not edges. Every overlay entry is a tab stop and counts as a marked node in `fitViewerContent`.
- Only open work was seeded at the `AddNodeExecutions` migration, with `IsCutoverSeeded`; finished historical visits were not backfilled. There is no durable record of when a deployment applied that migration. Every instance created since then has a non-seeded visit at its start event, which `AddInstanceAsync` writes in the creation transaction.
- `HistoryPrunedAt` covers several history categories and does not identify node-activity deletion specifically. Retention deletes node activity in bounded batches, keeps open visits and visits referenced by a token or a variable write, and advances the instance's `UpdatedAt` when it prunes.
- When a token moves along a flow, the visit it leaves is closed with `ExitedViaFlowId` in the same transaction (`UpdateExecutionTokenAsync`, plus the action, gateway, and message completions), and a new target visit records `EnteredViaFlowId`. Two sources have no exit record: a visit closed for an async-after wait gets a null exit before its continuation moves the token (`EnsureAsyncAfterWaitAsync`), and multi-instance parents have no visit.
- MI nodes have no parent node-execution row, and every child repeats the parent's entry flow. An async-before wait or an initially true interrupting conditional boundary can end an entry before children exist. The source visit's exit still records that entry, except from the two sources above.
- A version switch rebinds active/pending node executions, with their `EnteredViaFlowId`, to the target definition, while completed executions keep their previous definition. Target-side entries can therefore move to the new version; an exit is written under the version current at the move and never moves.
- `GET /api/instances/{id}` requires only an authenticated caller and returns the instance's `History`, including each row's flow, source and target nodes, and performer.
- Node-execution search already accepts `workflowId`, and `PagedResult.TotalCount` is a 64-bit JSON number.

These baseline findings guided the implementation. Runtime and browser verification are recorded separately below.

## Builds on

- The [UI-01 viewer and component boundaries](ui-01-live-instance-diagram.md#plan), including the read-only controller, selection callback, current-position overlay, and latest-reactivation boundary.
- [`node_executions`](../database-schema.md#node_executions), which records ordinary visits with their entry and exit flows, and MI child visits, regardless of `FlowInfo` usage.
- [`NodeExecutionQueryService`](../../Flowbit/src/Flowbit.Service/Services/NodeExecutionQueryService.cs) and [`NodeExecutionQueryRepository`](../../Flowbit/src/Flowbit.Infrastructure/Repositories/NodeExecutionQueryRepository.cs) for dynamic role resolution, per-definition visibility, and PostgreSQL queries.
- [`WorkflowRuntimeRepository`](../../Flowbit/src/Flowbit.Infrastructure/Repositories/WorkflowRuntimeRepository.cs) for entry/exit recording, instance creation, version rebinding, and observed-flow identities.
- [`RetentionRepository`](../../Flowbit/src/Flowbit.Infrastructure/Repositories/RetentionRepository.cs) for bounded cleanup, instance locks, reference protection, and transactional pruning markers.

Do not use `sequence_flow_occurrences` or `sequence_flow_summaries` as the universal source: ordinary routing writes them only when the definition uses `FlowInfo`, with administrative actions as an exception. Do not reconstruct node lifecycles from heterogeneous `instance_history` rows.

## Open questions

The plan resolves the original questions and the gaps found during review:

| Question | Decision |
| --- | --- |
| Who can see the path? | Anyone who can open instance detail, through the same check; execution lists and links keep node-activity authorization. See [Summary read boundary](#summary-read-boundary). |
| How are long histories loaded? | A grouped summary endpoint with graph-sized output, requested again only when the instance changed; existing paged search only for selected-node detail. |
| Which runs and versions? | All retained runs of the displayed exact definition, with the documented open-target rebinding exception in section 2. Latest-run positions stay independent. |
| How are missing visits identified? | A coverage flag classified once from surviving evidence, plus a node-activity-specific prune timestamp; conservative unknown/partial classification for older evidence. |
| How are entries before MI fan-out counted? | From the source visit's exit flow for ordinary sources, written when the token arrives, with no new runtime records. Async-after and MI sources use target-child evidence and report the documented gap before fan-out. |

### Summary read boundary

Decided on 2026-11-10: the summary uses the same access check as instance detail.

The summary holds node IDs, flow IDs, counts, and coverage metadata: no actors, roles, claims, variables, or visit timestamps. Every caller who can open instance detail already receives the `History` rows these facts come from, including each row's flow, source and target nodes, performer, and payload, so the summary discloses nothing new. Node-execution records stay restricted because they hold actor roles and claims, variable writes, durations, and failure text; execution lists and links keep node-activity authorization, unchanged.

- Implement the check that protects `GET /api/instances/{id}`, not "any signed-in user": register the route in the same `/api/instances` group and resolve the actor the same way, so a future restriction of instance detail, such as a tenant scope, also restricts the summary.
- Retention can delete workflow history while keeping node activity. The diagram can then show a path that the History table no longer lists; it is the same kind of information, and the UI guide says so.
- More callers can request the aggregation. The toggle starts off, and unchanged polls skip it.

Rejected: limiting the summary to node-activity readers (`NodeExecution.RequiredRole` or the displayed version's `taskAssignmentRoles`). It would hide nothing that the History table on the same page does not already show, and most task workers and reviewers would get a toggle that fails. Revisit it only together with restricting `History`.

## Plan

### 1. Scope and user experience

Add **Show recorded history** to the Diagram section, off initially to preserve UI-01's opening view and avoid unnecessary aggregation. Enabling it loads the summary and displays:

> Recorded history for workflow version {version}, across all retained runs. Records belonging to other versions are not shown. Current and final positions describe the latest run.

Keep the toggle choice only in the current instance page state. Refresh preserves it; navigation to another instance or an identity change resets it. A failed history request leaves current/final markers usable and offers a history-only retry.

The legend distinguishes **Reached**, **Flow taken**, and existing current/final states. Reached means arrival or visit evidence exists, not that an activity started or completed successfully. Pending async-before work can have an incoming flow. A node with both history and a current/final marker retains the stronger current/final ring and its progress/token badge.

Historical-only nodes get a subdued dashed ring; pointer selection already works for every node. Taken flows get a separate viewer-history stroke and traversal count; preserve arrowheads, labels, and error/automatic-flow distinctions. Provide exact counts in tooltips. Abbreviated visual counts must retain the exact accessible value.

Keep the canvas tab order limited to current/final positions, so a long history adds no tab stops. A collapsible **Recorded history** list beside the diagram, below it on narrow screens, names each reached node with its type and incoming/outgoing traversal counts, sorted by name and then ID. Each entry is a button that selects its node, and the list is the keyboard and screen-reader path to historical-only nodes. The selected node's panel lists its incoming and outgoing flows with counts.

Separate **Current/final positions** from **Recorded executions** in the selected-node panel. Show authorized executions 50 at a time, newest ID first, with previous/next navigation and the exact retained-record total. Label this total as execution records, not logical MI visits. A node reached only through an exit record, such as an MI entry that ended before work items existed, is still marked; explain that no retained execution detail is available. Keep node-only selection; no new flow-selection API is needed.

Non-goals: editing, execution commands, incident queries, new polling loops, provider calls, authored workflow properties, new traversal records, duration analytics, and reconstruction of deleted history. The existing multi-instance fan-out transaction must advance the instance revision when it creates child evidence, as confirmed by stage 1.

### 2. Evidence and aggregation rules

Use these rules in a focused query service/repository. Each traversal is counted from exactly one record, chosen by the flow's source node in the displayed definition:

| Flow's source node | Traversals are counted from |
| --- | --- |
| Any node except the two below | Visits of kind `node` at the source with `ExitedViaFlowId` equal to the flow. The exit commits with the move, under the version current at that moment, and is never rebound. It also covers MI entries that end before fan-out. |
| A node with `asyncAfter: true` | Visits of kind `node` at the target with `EnteredViaFlowId` equal to the flow. The source visit closes at the async-after wait with no exit. |
| A multi-instance user task | The same target-side rule; MI parents have no visit. |
| Either of the two above, into a multi-instance user task | One per distinct `MultiInstanceExecutionId` among the target's child visits with `EnteredViaFlowId` equal to the flow, pending children included. An entry that ends before children exist is not recorded; coverage reports `multiInstanceEntryUnrecorded` (section 3). |

A node is reached when a visit of any kind or status at it is attributed to the displayed definition, or when a counted traversal of the displayed definition ends at it. The second clause marks an MI task whose entry ended before work items existed. Visits with a null entry flow (start, boundary, reactivation, fork-created gateway copy, complex reset) mark their node and add no traversal.

1. Filter by instance and exact current definition. Aggregate the whole retained set in PostgreSQL; do not load visit pages to calculate totals.
2. Never add `SelectedFlowId`, MI votes, or `WinningFlowId` to traversal counts, and never count one traversal from both of its ends. They describe decisions, not extra traversals.
3. Validate IDs against the displayed definition: a counted record's node must be the flow's source (exit) or target (entry), and every node and flow must exist. Omit anything else and report `unmappedEvidence`; never match by name or use another version's geometry. Child visits of one MI execution with different entry flows produce `conflictingEvidence` instead of a confident count.
4. Include pending children in the MI entry count: the parent already reached the node. Do not describe pending children as individually traversing the flow or completing work.
5. Deduplicate node IDs. No per-node business-activation metric is added: gateway copies and MI children make raw row counts a different concept.
6. Loops increment counts for each committed traversal. Rollback contributes nothing, because the exit, the entry, and the move commit together.
7. Version attribution: a switch rebinds open visits, so an open visit always carries the instance's current definition until it exits, and an exit lands on the version current at the move. A task waiting across a switch is therefore reached on the new version, while the flow it arrived by stays on the old one, for ordinary and MI tasks alike. The exception is a target-side count whose target visit is still open at a later switch: its entry moves with the visit. Counting always uses the displayed source definition. A rebound open target contributes its entry only when that displayed source is async-after or MI. Changing a completed source across those categories can therefore add or omit its pre-switch incoming count; no earlier version is reconstructed.
8. Parent and called-child instances keep separate diagrams. Reuse child-instance links; do not aggregate a call tree into its parent's path.

### 3. Workflow model, persistence, and compatibility

#### Workflow JSON

No workflow JSON or schema-version change. `WorkflowModelMigrator`, editor import/export, authored properties, and definition validation stay unchanged. Overlay data stays outside the document and never enters saves or undo history.

#### Runtime recording

The summary reads the entry and exit flows that `node_executions` already records. MI parents still have no visit and retain null `CurrentNodeExecutionId`; routing, fan-out timing, and version rebinding are unchanged. One existing write transaction needs a revision fix: `AddMultiInstanceAsync` must advance its owning instance's `UpdatedAt` when it inserts child visits. PostgreSQL characterization confirmed that deferred fan-out after an async-after source can create the first reached-node and incoming-flow evidence without changing that timestamp. Update the already tracked owner in the existing fan-out transaction, so rollback also rolls back the revision and an idempotent continuation does not change it again.

Stage 1 also confirmed that durable service staging can change a visit from pending to active without changing instance `UpdatedAt`. That changes none of the summary's node, flow, version, or coverage facts. The caching invariant is therefore **every committed change to summary evidence advances the instance revision**, rather than every node-execution status change. Selected execution detail remains independently refreshed.

Stage 1 must confirm that every committed traversal from a source other than an async-after node or MI parent leaves exactly one exit record. Cover user and administrative actions, gateway splits and merges (including the arriving token's visit and fork-created copies), complex activation and reset, boundaries, message/timer/conditional catches, call completion, and scoped interrupts. Reactivation and boundary entry record no traversal. If a path records no exit, or two, stop and revise this plan: fix that path's recording in its existing transaction, or adopt the fallback below.

#### Coverage metadata

Add two columns to `workflow_instances`:

- `NodeActivityFromCreation`, a non-null boolean: node activity has been recorded since the instance was created. Add it with default `false`, then change the default to `true` in the same migration. Every instance created after `AddNodeExecutions` records from creation, whichever binary creates it, so `AddInstanceAsync` needs no change.
- `NodeActivityPrunedAt`, the nullable UTC time of the first actual deletion of node-activity evidence.

A separate migration classifies existing rows: `true` when the instance has a non-seeded visit at a start event (the visit `AddInstanceAsync` writes) and no `HistoryPrunedAt`. Instances created before the cutover have no such visit and stay `false`. The classification touches every instance row, so run it in bounded batches outside the column's transaction, in a migration of its own as `ValidateCronTimerScheduleConstraints` does, and measure it on a production-sized copy. Reactivation and version switching never change either column.

Derive coverage conservatively:

| Evidence | Coverage and reason |
| --- | --- |
| A surviving `IsCutoverSeeded` visit | `partial`, `cutover` |
| `NodeActivityPrunedAt` set | `partial`, `nodeActivityPruned` |
| Unmappable or conflicting retained evidence | `partial`, `unmappedEvidence` or `conflictingEvidence` |
| `NodeActivityFromCreation` false and no surviving seeded visit | `unknown`, `legacyCoverageUnknown` |
| `HistoryPrunedAt` set without `NodeActivityPrunedAt` on an instance not recorded from creation | `unknown`, `retentionCategoryUnknown` |
| The displayed definition has a flow from an async-after node or MI task into an MI task | `unknown`, `multiInstanceEntryUnrecorded` |
| None of the above | `complete` within the declared scope |

Return all applicable reasons; partial takes precedence over unknown. For an instance recorded from creation, `HistoryPrunedAt` alone never lowers coverage: from the upgrade on, every node-activity deletion also sets `NodeActivityPrunedAt`. Stage 1 confirmed that even a non-interrupting conditional boundary can terminate the instance before MI fan-out. Local `asyncBefore`/interrupting-boundary properties therefore do not establish safe entry coverage. Conservatively report `multiInstanceEntryUnrecorded` for every target-side-counted MI entry shape; the reason describes a recording limitation, not proof that an entry is missing. Narrowing it through topology analysis is deferred. Complete describes recorded coverage within this version/run scope, not a cross-version timeline, successful execution, or protection against unsupported direct database edits. Empty arrays alone never establish completeness.

#### Retention, locks, and rollout

No retention category or table changes. When a retention batch deletes `node_executions` rows, set `NodeActivityPrunedAt` in the statement that already sets `HistoryPrunedAt` and `UpdatedAt`. Other categories must not set it. Continue setting `HistoryPrunedAt`, preserving the reactivation prohibition. Retention keeps open visits and visits referenced by a token or a variable write, so pruned history is a skewed sample: the notice must not suggest that the remaining markers are the beginning or end of the path.

Summary reads take no runtime row locks, and no lock order changes.

The migrations are additive. Running API replicas write the same evidence and instance rows as before, so they need no coordination. Only the Worker deletes node activity, and an old Worker would delete it without setting the new marker: stop every Worker before applying the migrations and restart only upgraded Workers. Mixed Worker versions are unsupported. Upgrade the API before the UI so the UI never calls a missing route. No historical completeness backfill is promised beyond the classification above; rollback must preserve evidence.

#### Version switching

Keep active/pending node-execution rebinding unchanged; section 2, rule 7 defines how counts are attributed. `ListObservedSequenceFlowsAsync` already includes exit flows, so observed-flow compatibility checks need no change. A future historical-version selector must account for rebound visits.

#### Fallback: recorded MI arrivals

Adopt this only if stage 1 shows that exit records cannot carry the counts, and revise this plan first. The reviewed design was a `flowbit.multi_instance_node_arrivals` table, written in the `node.IsMultiInstance` branch of `UpdateExecutionTokenAsync`, the only path that moves a token onto an MI node. `AddExecutionTokenAsync` would need a guard, because it would create an MI parent visit if it were ever given an MI node. Rows would be keyed by token and activation, linked once at fan-out, retained under `nodeActivity`, and included in observed flows, and API and Worker writers would need a hard cut. That adds a runtime concept and would raise the effort to L.

### 4. API and query contract

Add **`GET /api/instances/{id}/diagram-history`**, returning `InstanceDiagramHistoryDto`. There is no paging, arbitrary version selector, or client-provided role scope; the server selects the current definition.

| Field | Meaning |
| --- | --- |
| `instanceId`, `workflowId`, `workflowVersion` | Instance and exact displayed definition |
| `instanceUpdatedAt` | Instance revision read with the summary, for stale-presentation detection and skipping unchanged refreshes |
| `asOf` | UTC snapshot time, not a promise of continuous live updates |
| `scope` | Fixed `currentVersionAllRuns` |
| `coverage` | `complete`, `partial`, or `unknown`; reason codes; optional `nodeActivityPrunedAt` |
| `nodes` | Unique `{ nodeId }` records, sorted by ID |
| `flows` | Unique `{ flowId, traversalCount }` records, sorted by ID; positive 64-bit counts |

No workflow JSON, actors, claims, roles, variables, payloads, incidents, or error text is added. Reuse the redacted definition already loaded by instance detail. Counts are 64-bit JSON numbers, like `PagedResult.TotalCount`. One instance's retained visits cannot approach JavaScript's safe-integer limit, so no string or BigInt encoding is needed.

- Resolve the canonical actor through `IActorContextResolver`. Missing authentication remains `401`; invalid actors retain existing error handling.
- Authorize exactly as `GET /api/instances/{id}` does ([decision](#summary-read-boundary)). No node-activity role check or dynamic role setting applies to the summary.
- A missing instance returns `404`. An empty summary returns `200`, empty arrays, and coverage metadata.
- Other errors use existing problem handling. The UI distinguishes a failed or unavailable summary from an empty result.

Create `IInstanceDiagramHistoryQueryService` and a focused repository port. Do not put aggregation in `WorkflowEngineService` or expand the ordinary detail payload. `NodeExecutionQueryService` and its authorization stay unchanged.

Read identity, authorization, coverage, and grouped evidence in one consistent snapshot: one SQL statement or a short read-only repeatable-read transaction. PostgreSQL groups and counts: reached node IDs, kind-`node` exits and entries per node and flow, distinct MI executions per child entry flow, MI executions with conflicting entry flows, and whether seeded visits survive. The service then applies section 2 with the displayed definition. Bind parameters. Avoid per-node queries, materializing all visits, and the unrelated joins of the rich node-execution projection.

Output size is bounded by the displayed graph, while database work grows with retained evidence. Measure large-history plans against the existing `(InstanceId, Id)` and `(InstanceId, UpdatedAt, Id)` indexes; add an `(InstanceId, WorkflowDefinitionId, ...)` index only when justified. Cancellation/timeouts fail visibly, never return truncated counts. No rollup tables, caches, or aggregation Worker are planned.

### 5. Flowbit.Ui and viewer implementation

| Location under `Flowbit/src` | Change |
| --- | --- |
| `Flowbit.Shared/Dtos` | Summary/coverage DTOs and wire contract |
| `Flowbit.Ui/Clients/WorkflowApiClient` | Typed cancellable summary read with status distinctions |
| `Flowbit.Ui/Components/Pages/InstanceDetail.razor` | Toggle, requests/retry, unchanged-poll skip, selected-node pagination, refresh and identity guards |
| `Flowbit.Ui/Components/Shared/InstanceDiagramOverlay.cs` | Pure history/current-state merge and deterministic node/flow signature |
| `Flowbit.Ui/Components/Shared/InstanceDiagram.razor` | Controls, legend, notices, textual history list, selected-node flows; viewer lifecycle only |
| `Flowbit.Ui/Components/Shared/InstanceDiagram.razor.css` | Responsive controls, notices, list, and details |
| `Flowbit.Ui/wwwroot/editor/flowbit-editor.js` | Separate history overlay map, edge/node redraw, selection brought into view |
| `Flowbit.Ui/wwwroot/editor/flowbit-editor.css` | Viewer-scoped history rings/edges/counts |
| `Flowbit.Ui/wwwroot/workflow-editor-host.js` | Forward the extended overlay if required |

#### Page coordination

- Load history only while enabled: toggle-on, explicit refresh/retry, existing eligible poll refreshes, and successful actions/reactivation/version changes. No additional timer or UI-05 dependency.
- Skip a poll's summary request when the refreshed detail's `UpdatedAt` equals the displayed summary's `instanceUpdatedAt`; transitions, multi-instance fan-out, and retention advance it. Toggle-on, explicit refresh/retry, actions, and identity changes always request. Stage 1 confirms that every committed change to summary evidence advances the instance's `UpdatedAt`; a pending-to-active status change alone does not change that evidence.
- Use the page's refresh/selection lifecycle. Capture instance ID, definition ID, identity epoch, and request generation. Cancel superseded requests and reject obsolete responses even if cancellation loses a race.
- Match summary identity/version/`instanceUpdatedAt` to displayed detail. On mismatch, clear history and allow at most one coordinated refresh; continued churn shows retry state, not a refresh loop.
- Immediately clear counts, execution detail, and overlays on identity change. Clear execution detail when its lookup observes permission loss; node-activity role-setting changes take effect on the next lookup. Previous-identity responses cannot restore data. Disposal stops requests and callbacks.
- A summary `404` clears history like any failed request; the page's own detail refresh reports the missing instance.
- History errors do not remove current positions or block task actions. Clear failed/stale history rather than present it as current, and offer a local retry/status message.
- Keep UI-01's bounded latest-execution lookup and current-position link collection independent from historical paging. Its role-scoped lookup can include earlier definitions, which an async-after wait may still reference. Paging history must never replace a current token's link with an earlier visit or remove its MI child links.
- Only the new historical list adds `workflowId` to `instanceId`/`nodeId`. Keep all statuses, `id desc`, and page size 50; reset paging on node/version/identity change. Load it while history is enabled, preserve existing history-off details, and show lookup errors instead of claiming zero visits.

#### Overlay and interaction

Extend overlays compatibly with reached-node flags and a `flows` collection, kept in a viewer map separate from the position markers. Preserve `active`, `ended`, `faulted`, and `cancelled` states; history is independent. Old node-only overlays remain valid. Safely ignore malformed entries, unknown IDs, and invalid counts.

Both `load(definition, overlay)` and `setOverlay(overlay)` redraw edges and nodes. Clearing history removes historical decorations but keeps position markers. Preserve the controller's `load`, `setOverlay`, `selectNode`, `dispose`, and node-only selection callback.

Do not reuse authoring trace or `.trace-active`. Use SVG text/title and escaped labels, never injected HTML. Preserve type outlines, selection rings, Enter/Space, Escape/Clear, focus restoration, and textual equivalents for colors/counts. Historical-only nodes are not tab stops; selecting one from the textual list calls `selectNode` and brings it into view without changing the zoom.

History never affects the fit: `fitViewerContent` keeps using current/final positions only. Toggle/count updates, detail paging, and normal refresh preserve user pan/zoom. Keep plain-wheel page scrolling, Ctrl/Meta-wheel zoom, drag-to-pan, and read-only shortcut protections.

Keep ownership intact: `InstanceDetail` owns API/auth/selection; `InstanceDiagram` injects only `IJSRuntime` and owns viewer lifecycle; the helper is pure; JS owns its mounted subtree. Preserve failed-mount/update gating to avoid render/retry loops.

### 6. Delivery sequence

1. **Characterize evidence; this stage decides whether the plan holds.** Add focused scenarios for every routing path listed in section 3, loops, MI entries (including async-before cancellation and initially true interrupting and non-interrupting conditional boundaries), async-after and MI sources, and version switches with ordinary and MI tasks waiting. Confirm with committed data, including definitions without `FlowInfo`, that each traversal has exactly one counted record and that every change to summary evidence advances the instance's `UpdatedAt`. The initial 2026-11-10 characterization passed 48 PostgreSQL checks and found the deferred fan-out revision gap described above. After fixing it in the existing transaction, the 49-case gate passed before summary implementation. The same checks disproved the local-property coverage predicate; retain the conservative target-side MI reason from section 3. Any further failure stops implementation until this plan is revised.
2. **Add coverage metadata.** Add the two columns, the classification migration, and the retention marker. Verify upgrade classification, defaults for instances created by old and new binaries, and the marker's transaction.
3. **Implement the summary.** Add the focused query/service, instance-detail authorization, route/DTOs, client, API tests, and large-history query-plan evidence.
4. **Render history.** Extend the pure overlay/shared viewer, then page coordination, notices, controls, the textual list, and selected-node paging. Keep history-off regressions passing.
5. **Verify and document.** Run automated, browser, and Worker-enabled cases; update owning guides with actual behavior and limitations.

Each stage should be independently reviewable. Do not mark Done until MI entries, the read boundary, retention notices, stale-response protection, and browser interactions pass. A missed supported traversal requires a recording fix or explicit acceptance-scope revision before release, never an inferred connector.

### 7. Documentation during implementation

| Owning document | Required update |
| --- | --- |
| [Flowbit.Ui guide](../ui-guide.md#read-the-instance-diagram) | Toggle, legend/counts, textual history list, scope, paging/retry, coverage notices; history for everyone who can open the instance versus execution links for node-activity readers; a path the History table no longer lists after retention; replace the statement that earlier visits/flows are never shown |
| [API reference](../api-guide.md) | Route, DTOs, instance-detail authorization, errors/empty response, counts/coverage; compare registration, tests, and development OpenAPI |
| [Developer guide](../developer-guide.md) | HTTP-only history integration and limits |
| [Database schema reference](../database-schema.md) | `NodeActivityFromCreation` and `NodeActivityPrunedAt` on `workflow_instances`, with their classification and default; how entry and exit flows count traversals |
| [Deployment and operations](../deployment.md) | Migration order, stopping Workers before the migrations, batched classification, legacy coverage, node-activity pruning marker |
| [Runtime reference](../../Flowbit/README.md) and [AGENTS.md](../../AGENTS.md) | Aggregation ownership and counting rule, viewer overlay, version/retention invariants, current no-history statements |
| [Browser smoke README](../../Flowbit/tests/Flowbit.BrowserTests/README.md) and [acceptance README](../../Flowbit/tests/Flowbit.BrowserAcceptanceTests/README.md) | Actual coverage, commands, screenshots, durable evidence cases |
| [Root README](../../README.md) | Capability description and any screenshot made misleading by the shipped change |
| This page and [backlog](README.md) | Status, final criteria/evidence, separate follow-up items for remaining scope when Done |

Update [BPMN support](../bpmn-support.md) or [node reference](../node-reference.md) only if implementation changes documented execution semantics; none is intended. Update examples/catalog only if adding a committed demonstration. Preserve UI-01's historical first-release description and link to this extension.

The owning guides now describe the implemented behavior. UI-01 remains the historical first-release record and links to this extension.

### 8. Tests and verification

#### Automated acceptance matrix

| Area | Required evidence |
| --- | --- |
| Routing paths | Exactly one counted record per traversal for user and administrative actions, gateway splits/merges (arriving token and fork copies), complex activation/reset, boundaries, message/timer/conditional catches, call completion, and scoped interrupts; none for start, boundary entry, reactivation, or other null-entry visits |
| Ordinary paths | Linear, loop, exclusive, parallel/inclusive split/join, complex reset, cancellation, fault, boundary, reactivation; no invented null-entry flows |
| MI paths | Parallel/sequential, pending children, repeated node entry, votes versus actual traversal, interruption; async-before wait/cancellation and initially true conditional interruption before fan-out, counted from the source exit and marked reached; entries from async-after and MI sources, counted from children and reported by the coverage reason |
| Transactions | Rollback creates no evidence; durable retry/lease replay counts one traversal |
| Source independence | Equivalent paths with/without `FlowInfo` produce equivalent counts |
| Versions/calls | Current-version geometry; ordinary and MI tasks waiting across a switch are reached on the new version while their incoming flows stay on the old one; a traversal after the switch counts on the new version; the documented target-side exception; partial MI completion across a switch; separate child history |
| Coverage/migrations | Post-cutover legacy instance classified `true`; cutover-seeded and pre-cutover instances `false`, including one whose only open work at the cutover was an MI parent before fan-out; instance pruned before the upgrade `unknown` with `retentionCategoryUnknown`; new instances `true` from old and new binaries; other-category pruning keeps `complete`; reactivation and version switching never change either column; the MI-entry shape reports `multiInstanceEntryUnrecorded` |
| Retention | Partial/full deletion, protected rows, unrelated pruning, running-instance protection, marker and `UpdatedAt` set in the deleting transaction |
| Authorization | Anonymous `401`, invalid actor, missing instance `404`, empty `200`; an ordinary reviewer reads the summary but gets no execution links, while a node-activity reader gets both; summary and instance detail return the same status for each caller |
| Query/load | Exact counts, deterministic graph-sized output, no truncation or per-node queries, cancellation, consistent snapshot, unmappable/conflicting evidence |
| UI state | Lazy loading, toggle-off clearing, error/retry, unchanged-poll skip and forced requests, observed permission loss, stale selection/page/version/identity, post-action refresh, latest-run precedence, disposal; paging history cannot change current-position links, including a revisited node and an async-after completed visit |
| Viewer | Edge load/set/clear, separate history map, invalid overlays, escaping, unchanged tab order and fit, list-driven selection into view, preserved pan/zoom, editor regressions |

Extend `InstanceDiagramOverlayTests`, `InstanceDiagramPageTests`, `InstanceDiagramComponentTests`, `EditorViewerModeTests`, and `WorkflowEditorHostViewerTests`. Build on `NodeExecutionLifecycleApiTests`, `NodeExecutionLifecycleCoverageTests`, `NodeExecutionMigrationTests`, authorization, version-compatibility, and retention tests. Add focused evidence-characterization and summary test classes rather than coupling persistence checks to render tests.

Use real PostgreSQL for grouping, authorization, classification, retention, migrations, and concurrency. Benchmark a small path and a 100,000-record loop/MI history, recording hardware, query plans, response size, and repeated timings. Target a warmed summary response within one second on the documented local benchmark stack; investigate misses instead of reducing counted evidence. This is a validation target, not a production SLA.

Commands from repository root, with identical arguments in PowerShell 7 and Bash:

```text
dotnet test Flowbit/tests/Flowbit.Tests/Flowbit.Tests.csproj -c Release
dotnet publish Flowbit/src/Flowbit.Api/Flowbit.Api.csproj -c Release -o artifacts/browser/hosts/api /p:UseAppHost=false
dotnet publish Flowbit/src/Flowbit.Ui/Flowbit.Ui.csproj -c Release -o artifacts/browser/hosts/ui /p:UseAppHost=false
dotnet build Flowbit/tests/Flowbit.BrowserTests/Flowbit.BrowserTests.csproj -c Release
pwsh Flowbit/tests/Flowbit.BrowserTests/bin/Release/net10.0/playwright.ps1 install chromium
dotnet test Flowbit/tests/Flowbit.BrowserTests/Flowbit.BrowserTests.csproj -c Release --no-build --no-restore --filter FullyQualifiedName~InstanceDiagramSmokeTests
dotnet test Flowbit/tests/Flowbit.BrowserTests/Flowbit.BrowserTests.csproj -c Release --no-build --no-restore
git diff --check
```

Follow [browser prerequisites](../../Flowbit/tests/Flowbit.BrowserTests/README.md#prerequisites), including Docker and Linux browser dependencies where applicable. Actual execution results and limitations are recorded in the implementation validation record below.

#### Real-browser verification

Extend R8 diagram smoke coverage and retain history-off assertions. Use Chromium with actual API/UI hosts on localhost at **1440×900, 1024×768, and 390×844**. Record actual ephemeral localhost URLs in the test report.

Exercise real controls: history toggle; historical-only selection by click and through the textual list with Tab/Enter/Space; visit paging; flow counts; Clear/Escape; pointer pan; buttons/Ctrl-wheel zoom; plain-wheel page scrolling; refresh after an action; execution/child links and navigation back. Inspect overflow, overlapping badges, clipped counts, lost focus, and current-state readability. Confirm Delete/undo/connect shortcuts cannot edit the viewer.

Test a node-activity reader and an ordinary reviewer (both see the history; only the reader gets execution links), identity changes during delayed requests, terminal/reactivated instances, and version changes with history on. Check console errors/warnings. Retain screenshots of history off/on, loop/branch, MI entry, coverage notice, selected details, the textual list, and narrow layout.

Use the separate [Worker-enabled acceptance suite](../../Flowbit/tests/Flowbit.BrowserAcceptanceTests/README.md) for async-before, async-after, and durable boundary/call cases. A real node-retention browser case requires an authentically aged instance and remains a separate deployment follow-up; neither fresh-stack suite currently covers it. Keep the smoke fixture Worker-free. Browser setup uses real API actions; gates may delay real responses unchanged but cannot inject app state or edit database rows to manufacture acceptance. Isolated migration tests may prepare historical schemas; they do not substitute for browser evidence.

Jint, render tests, and simulated DOMs do not satisfy [mandatory real-browser verification](../../AGENTS.md#mandatory-real-browser-ui-verification). The implementation completion report must separately state automated results, browser/URLs/interactions/viewports/console results, screenshots, and unavailable checks. Validate changed links, anchors, images, and any added JSON examples before marking Done.


## Implementation validation record — 2026-11-10

The implementation keeps the approved boundaries: a focused read-only SQL query/service and endpoint, two additive coverage migrations, the existing retention transaction, separate viewer-history maps, and page-owned requests. The only runtime evidence change advances the instance revision during multi-instance fan-out; no traversal table, workflow JSON property, authorization change, or background polling loop was added.

The evidence gate passed **49/49** PostgreSQL scenarios before summary implementation. Coverage/classification, retention, rollback, and MI regression checks then passed **36/36**. The migration fixture classified 2,009 instances in three bounded batches in 45 ms on the local disposable database. This is not a production-sized migration estimate.

The isolated loop benchmark held 100,002 node visits; the MI benchmark held 100,004. Five warmed authenticated HTTP requests took 103–110 ms and 124–132 ms respectively, returning 373 and 412 bytes. PostgreSQL EXPLAIN ANALYZE execution times were 129.945 ms and 157.856 ms. Small-history requests took 4.3–6.1 ms. The stack was PostgreSQL 17.10 (postgres:17-alpine), .NET 10.0.9, Windows 10.0.19045, AMD Ryzen 5 5600G host with four logical CPUs exposed and 32 GiB memory. Existing indexes were sufficient. These are local measurements, not a production SLA. Query plans, SQL, and response/timing JSON are retained under local artifacts/diagram-history/20261110-080357-loop-092db45d1c6f481c9e07d541eae3a6d4 and artifacts/diagram-history/20261110-080332-mi-b2614fc119b043c582e952f24e39f379. The [isolated benchmark command](../../Flowbit/README.md#instance-diagram-summary-benchmark) enforces the one-second target without loading the shared runtime-test database.

The Worker-enabled Chromium acceptance run passed **7/7**, followed by a strengthened delayed-identity case **1/1**. It exercised real version-form confirmation and automatic exact-version history refresh, deferred MI fan-out, boundary paths, separate call parent/child histories, and delayed real summary/current/historical responses after identity replacement. The local UI ports were 57897, 58234, and 58493; viewport coverage was 1440×900, 1024×768, and 390×844. Console errors, warnings, failed requests, and unexpected dialogs were all zero. Evidence is under local artifacts/acceptance/runs/20261110-082208-4e1d46af and artifacts/acceptance/runs/20261110-082723-e679b48a.

The broad test attempts also exposed test isolation and harness issues: global node-activity role settings affected an authorization assertion, large synthetic history shared a runtime-test database, and a durable conflict test could await a service-entry signal forever after its background lease assertion had already failed. The new authorization fixture has an explicit per-version admin scope, the load benchmark owns a separate PostgreSQL fixture, and the existing gate now observes processing failure and has bounded cleanup. A historical-schema integrity fixture also explicitly supplies the new columns when testing with the current EF model. The original full unit run was stopped after the unbounded wait; it is not reported as passing. The final focused run passed 163 of 164 cases; its only failure was a new OpenAPI assertion assuming a scalar JSON Schema type where OpenAPI 3.1 emits a nullable type array. That assertion was corrected and is checked separately below. All history service/API, page lifecycle, viewer, coverage/migration, retention, routing, and auxiliary-data regression cases passed.

Unavailable deployment evidence remains explicit: the fresh browser stack cannot produce authentically aged node-activity retention within the minimum one-day retention policy, and no production-sized restored database copy was supplied for migration timing. PostgreSQL deletion/protection/rollback tests and rendered coverage notices passed, but they do not replace that aged Worker/browser walkthrough or production-copy measurement. No clock override, injected application state, or database-row edit was used to manufacture browser acceptance. These checks remain on the [deployment checklist](../deployment.md#instance-diagram-history-deployment-checks); the agreed completion scope above permits UI-14 to be Done while they remain outstanding.


### Initial implementation verification results

- Focused feature/regression checks: **164 distinct cases passed** across the final 163-pass run and the corrected OpenAPI test rerun. The extra OpenAPI rerun passed 1/1 and retained the integer/int64 contract assertion. Reports: local artifacts/diagram-history/final-tests/ui14-final-focused-r2.trx and artifacts/diagram-history/test-results/diagram-history-openapi-final.trx.
- Isolated timing enforcement: **2/2 passed** with FLOWBIT_DIAGRAM_HISTORY_ENFORCE_LATENCY=1. The final MI requests took 137–261 ms (412 bytes), and loop requests 144–263 ms (372 bytes), under concurrent regression-test load. Exact counts and the one-second target passed. Plans/timings: local artifacts/diagram-history/20261110-084558-mi-754561146feb475a9b9922979333e123 and artifacts/diagram-history/20261110-084637-loop-b5cd6bafdacd4206819186f576d1b42f.
- Additional isolated regression classes passed: migration 3/3, error boundaries 62/62, durable processor 12/12, job repository 17/17, job conflict 11/11, advanced variable search 16/16, and MI evidence/cleanup 17/17. The manually composed search-test host now registers the new route service. The expression-timer class passed 12/14 together; both remaining failures were foreign-job lease assertions in its shared test queue and passed individually in fresh fixtures. At this stage the complete unit suite had **not passed as one clean run**; the follow-up results are recorded below. Logs/reports: local artifacts/ui14-regressions/results and artifacts/ui14-regressions/individual-results.
- Full Chromium smoke baseline: 120/122 passed. An identity-clear expectation was updated to assert the removed protected diagram/actions; an unrelated OIDC navigation timing failure passed unchanged on rerun. Final **headed Chromium 151.0.7922.34 passed 12/12**: all nine R8 diagram scenarios, R3 identity reset, and both OIDC modes. UI http://127.0.0.1:59491, API http://127.0.0.1:59486, standalone editor http://127.0.0.1:59497. Viewports: 1440×900, 1024×768, 390×844. Real interactions included history on/off, historical-node canvas/list selection with Tab/Enter/Space, paging beyond 50 visits, permissions and identity reset, pan/fit/zoom, ordinary-wheel page scrolling, and Ctrl-wheel zoom. No page/console errors, warnings, failed requests, or unexpected dialogs. Screenshots visually checked for desktop and phone layout; local evidence: artifacts/browser/runs/20261110-083835-43c5e8a0.
- Changed relative documentation links/anchors and the new API JSON example were validated; git diff --check passed. No workflow example or schema-version change was needed.

The focused command (PowerShell 7 and Bash use the same arguments) was:

```text
dotnet test Flowbit/tests/Flowbit.Tests/Flowbit.Tests.csproj -c Release --artifacts-path artifacts/ui14-validation --filter 'FullyQualifiedName~InstanceDiagram|FullyQualifiedName~EditorViewerModeTests|FullyQualifiedName~WorkflowEditorHostViewerTests|FullyQualifiedName~OpenApiContractTests|FullyQualifiedName~InstanceVariableUpdateUiContractTests|FullyQualifiedName~RetentionRepositoryTests|FullyQualifiedName~RetentionConcurrencyTests|FullyQualifiedName~DiagramHistoryRoutingEvidenceTests' --logger 'trx;LogFileName=ui14-final-focused-r2.trx' --results-directory artifacts/diagram-history/final-tests --nologo --verbosity quiet
```

The benchmark used the [documented isolated command](../../Flowbit/README.md#instance-diagram-summary-benchmark), with the same artifact output path and no-build/no-restore after that build. Browser and Worker commands are in their owning [smoke](../../Flowbit/tests/Flowbit.BrowserTests/README.md) and [acceptance](../../Flowbit/tests/Flowbit.BrowserAcceptanceTests/README.md) guides. Existing dependency advisory/analyzer warnings were reported by the builds; no new package was introduced.

A final screenshot-stability rerun passed **3/3** headed loop scenarios after waiting for the selected-node execution response before capture. The complete three-row textual list was visually inspected at desktop and phone widths; diagnostics remained empty. Evidence: local artifacts/browser/runs/20261110-084702-5ad49135, using UI http://127.0.0.1:60518, API http://127.0.0.1:60516, and editor http://127.0.0.1:60520. All test hosts were disposed after verification. The final documentation check covered 55 changed relative links/anchors with no failures.


### Review corrections — 2026-11-10

The follow-up review found and corrected four gaps:

- The history query now normalizes its freshly deserialized definition with `WorkflowModelMigrator`, matching instance detail for supported older JSONB snapshots. A persisted-legacy regression starts and completes an instance through real HTTP, checks exact counts, and verifies that reading history does not rewrite the stored definition.
- A successful current-execution lookup with zero available records now clears the historical page and invalidates any pending historical response. This also covers permission loss between the historical page response and its independent current-execution recheck. A failed lookup remains an error, rather than an empty authorized result, and obsolete current responses cannot clear newer data.
- Previous/Next execution controls stay mounted and focusable during loading and at page boundaries. They announce their unavailable state and ignore activation while unavailable. This preserves keyboard focus without allowing duplicate paging requests.
- The old implementation description is explicitly labelled as the baseline before implementation; the version-scope decision includes the documented rebinding exception.

The corrected backend passed **29/29** query/API cases, and the page/component checks passed **41/41**, including the new permission and pager regressions.

Final **headed Chromium 151.0.7922.34 passed 5/5** follow-up scenarios at 1440×900, 1024×768, and 390×844, using UI http://127.0.0.1:62494 and API http://127.0.0.1:62487. Enter/Space paging held genuine API responses and preserved focus during loading and at both boundaries; repeated activation while unavailable made no extra request. A real role-setting change between the authorized historical response and the current lookup removed execution links while preserving the summary. Real reactivation kept earlier runs visible and refreshed current/final positions and traversal counts. Console errors, warnings, failed requests, unexpected dialogs, and page overflow were all zero. Desktop, tablet, and phone captures were inspected, including readable pager focus captures. Evidence: local artifacts/acceptance/runs/20261110-095625-c3099e18; report: artifacts/ui14-browser-fixes/headed-results/ui14-review-headed.trx. The test hosts were disposed afterward.

The first fresh full-suite attempt finished with 4,565 passed and 54 failed. It exposed shared-queue interference from fixed-date timer fixtures, two historical migration fixtures missing the current EF model's new coverage columns, and a source-inspection test that assumed build output lived below the solution directory. These are test-fixture corrections; production lease scheduling and migration behavior remain unchanged. Report: local artifacts/diagram-history/review-fixes-full/ui14-review-fixes-full.trx.

The fixture corrections passed **6/6** focused cases: all five administrative migration tests and the source-path regression. The timer fixture now cancels only its own unfinished instances through the normal runtime API after assertions, preventing fixed-date timers from leaking into later tests' shared queue. No runtime lease selection or test behavior assertion was weakened.

The second full run passed **4,591/4,619**: all 14 expression-timer cases, all five historical administrative migration cases, and the source-path regression passed. Its remaining 28 failures were administrative tests' global-queue job-ID assertions, before their behavior assertions. Fixed-date business-calendar scenarios also left overdue timer jobs in that shared queue. Report: local artifacts/diagram-history/review-fixes-full-r2/ui14-review-fixes-full-r2.trx.

A focused combined calendar/administrative run reproduced that interference with **31 passed and 26 failed**. Read-only inspection identified the three overdue calendar timers being leased by later tests. Applying the same owned-instance runtime cancellation to the calendar fixture made the identical group pass **57/57**. Before/after reports and the observed timer rows are retained in local artifacts/diagram-history/calendar-admin-repro and artifacts/diagram-history/calendar-admin-fixed.

The full run at that review stage completed in 12.29 minutes with **4,618 passed and one failed** out of 4,619. All instance-diagram checks, both 100k-history benchmarks, OpenAPI, migration/source-path regressions, all 14 expression-timer cases, all nine business-calendar cases, and all 124 administrative cases passed. The sole failure was the unchanged `WebhookDispatchTests.ReceiverFailuresLeaveTheInstanceItsJobsAndItsIncidentsUntouched`: its second delivery acquisition returned no ready lease after the first send succeeded. It passed in both preceding full runs. Report: local artifacts/diagram-history/review-fixes-full-r3/ui14-review-fixes-full-r3.trx. This full run is not reported as clean.

That webhook case passed **1/1** in a fresh isolated rerun without source changes; report: local artifacts/diagram-history/webhook-isolated-rerun/webhook-isolated-rerun.trx. All disposable test hosts were closed. Documentation validation at that stage checked **55** changed relative links/images and their anchors with zero failures; `git diff --check` passed. The aged-retention browser walkthrough and production-copy migration timing remain unavailable as described above.

After building that stage's sources into the same artifacts directory, its full-suite command was:

```text
dotnet test Flowbit/tests/Flowbit.Tests/Flowbit.Tests.csproj -c Release --no-build --no-restore --artifacts-path artifacts/ui14-validation --logger 'console;verbosity=normal' --logger 'trx;LogFileName=ui14-review-fixes-full-r3.trx' --results-directory artifacts/diagram-history/review-fixes-full-r3 --blame-hang-timeout 5m --blame-hang-dump-type none
```

The isolated webhook rerun used the same build with `--filter FullyQualifiedName=Flowbit.Tests.WebhookDispatchTests.ReceiverFailuresLeaveTheInstanceItsJobsAndItsIncidentsUntouched` and its own results directory. No production webhook code or test assertion was changed.

### Further review corrections — 2026-11-10

The next review corrected the history-off fit calculation: it preserves the
existing current/final position halos and badges, accounts for event captions
and translated coordinates, and excludes the new history/count decorations and
selection from fit bounds. Repeated Fit also works after semantic zoom hides
labels. Compact counts carry into the next suffix (`999950` displays as `×1m`)
while accessible text keeps the exact value; a count of one uses singular text
in the selected-node panel.

Missing instance detail now has a distinct `404` message and address/list
guidance. Authentication/permission failures clear protected data with identity
guidance, including a `401` or `403` while a local history retry reloads detail.
Refresh failures that leave history enabled show a local error and
retry control; recovery reloads detail and matching history while preserving
the toggle. A missing matching snapshot also offers retry instead of an
indefinite loading state.

The documentation corrections replace the old no-history description, add the
summary route to both API inventories, provide all four linked DTO schema
tables, explain identity clearing and error recovery, correct the browser
retention limitation, and at that review stage used the backlog's defined
**In progress** status. The completion scope above records the subsequent
decision to mark UI-14 **Done** with separate deployment follow-ups.
Generated OpenAPI was captured through the existing in-memory documentation
host without an API listener or database. All 15 fields across the four tables
match its requiredness, nullability, formats, and schema references; the guide's
existing numeric-binding note accounts for OpenAPI's numeric-string alternatives.

Targeted automated checks for these corrections passed:

- **64/64** instance-diagram page, history-page, and component cases, including
  `401`/`403` during retry; report:
  local artifacts/ui14-errors-fixes/results/ui14-errors-fixes-final.trx.
- **44/44** adjacent reactivation, version-change client, reactivation client,
  and instance-detail display cases; report:
  local artifacts/ui14-errors-fixes/results/ui14-adjacent-ui.trx.
- **71/71** focused PostgreSQL migration-classification, summary API,
  routing/catch/end evidence, and overlay cases in artifacts/ui14-fixes-routing.
- **13/13** viewer/layout/rendering cases, followed by **3/3** headed real-SVG
  fit cases; browser report:
  local artifacts/ui14-fit-fixes/results/fit-browser-final.trx.

The four unit/integration groups total **192 passed**.

The final page/component command, including both authorization-retry cases, was:

```text
dotnet test Flowbit/tests/Flowbit.Tests/Flowbit.Tests.csproj -c Release --artifacts-path artifacts/ui14-errors-fixes --filter "FullyQualifiedName~InstanceDiagramHistoryPageTests|FullyQualifiedName~InstanceDiagramComponentTests|FullyQualifiedName~InstanceDiagramPageTests"
```

The fit browser run used Chromium **151.0.7922.34**, a **1024×650** browser
viewport, an **800×448** canonical viewer and **900×448** translated-coordinate
and long-caption viewers. It checked absolute zoom, all five nodes remaining
visible, initial and repeated Fit, history on/off and reload, zoom-out/Fit
recovery, and compact/exact count text. The static localhost hosts were
http://127.0.0.1:52400, http://127.0.0.1:52413, and http://127.0.0.1:52421.
All three browser diagnostics were empty. Screenshots and measurements are in
local artifacts/browser/fit/20261110-140449-be252dcb,
20261110-140454-85ed727d, and 20261110-140456-891af31d. The focused command and
prerequisites are in the [smoke README](../../Flowbit/tests/Flowbit.BrowserTests/README.md#instance-diagram-fit-checks).

The new error-state checks passed **9/9** headed browser cases at **1440×900,
1024×768, and 390×844**, plus **1/1** smoke-isolation guard. The three real
missing-instance cases used Chromium **151.0.7922.34**, UI
http://127.0.0.1:53188 and API http://127.0.0.1:53185; report:
local artifacts/ui14-error-browser-results/ui14-error-browser-missing.trx.
The six real outage/recovery cases used UI http://127.0.0.1:53309 and API
http://127.0.0.1:53305; report, including the guard:
local artifacts/ui14-error-browser-results/ui14-error-browser-outages.trx.
All nine browser diagnostics were empty. Expected API `404` and `500` responses
were recorded unchanged by the proxy. The acceptance-only helper stopped and
resumed the same disposable PostgreSQL container on its assigned port; it
neither changed rows nor injected responses and permits this operation only in
an acceptance stack without a running Worker. Earlier harness failures from a
changed restart port and a navigation helper that expected successful detail
were corrected before these final passing runs.

Missing-instance screenshots and request evidence are under local
artifacts/acceptance/runs/20261110-141506-96bfd1be; outage errors and successful
recovery captures are under artifacts/acceptance/runs/20261110-141611-b04b2eba.
Captures include `friendly-instance-not-found`, `local-refresh-error`, and
`local-retry-restored-history`, with panel images. The final commands were:

```text
dotnet test Flowbit/tests/Flowbit.BrowserAcceptanceTests/Flowbit.BrowserAcceptanceTests.csproj -c Release --no-build --no-restore --artifacts-path artifacts/ui14-error-browser-build --environment FLOWBIT_BROWSER_HEADED=1 --filter FullyQualifiedName~MissingInstanceShowsFriendlyNotFoundWithoutIdentityAdvice
dotnet test Flowbit/tests/Flowbit.BrowserAcceptanceTests/Flowbit.BrowserAcceptanceTests.csproj -c Release --no-build --no-restore --artifacts-path artifacts/ui14-error-browser-build --environment FLOWBIT_BROWSER_HEADED=1 --filter "FullyQualifiedName~RealRefreshFailureShowsLocalRetryAndReloadsDetailWithHistory|FullyQualifiedName~DefaultSmokeStackRejectsWorkerWithoutStartingResources"
```

Before the final authorization-retry correction, a headed runtime smoke run
passed **10/10**: all nine existing diagram cases and the R3 identity-replacement case.
It exercised current/final markers, work links, read-only interactions, history
counts, paging, keyboard access, and multi-instance entry/coverage at
**1440×900, 1024×768, and 390×844**. Chromium **151.0.7922.34** used UI
http://127.0.0.1:53487, API http://127.0.0.1:53485, and editor
http://127.0.0.1:53489. All browser diagnostics were empty and the hosts were
disposed. Screenshots and the manifest are under local
artifacts/browser/runs/20261110-141754-10024848; report:
artifacts/ui14-fit-fixes/runtime-results/ui14-final-runtime-smoke.trx.

After building the smoke project into the same artifacts directory, the command was:

```text
dotnet test Flowbit/tests/Flowbit.BrowserTests/Flowbit.BrowserTests.csproj -c Release --artifacts-path artifacts/ui14-fit-fixes --no-build --no-restore --filter "FullyQualifiedName~InstanceDiagramSmokeTests|FullyQualifiedName~RuntimeSmokeTests.R3_IdentityReplacementUpdatesProtectedActions" --environment FLOWBIT_BROWSER_HEADED=1 --logger "trx;LogFileName=ui14-final-runtime-smoke.trx" --results-directory artifacts/ui14-fit-fixes/runtime-results
```

A final authorization-retry review added the `401`/`403` page regressions in
the 64-case group above: clearing protected data must also set the page's
unavailable error when authorization fails during local retry. On the corrected
UI, an additional **1/1** headed **390×844** browser case passed with Chromium
**151.0.7922.34**, UI http://127.0.0.1:53930 and API
http://127.0.0.1:53924. A short-lived fixture token was accepted through the
normal token screen and API, a real database outage produced `500`, and the
same token was rejected with `401` after its expiry and the API's normal
60-second clock skew. Keyboard history Retry then displayed the unavailable
state and identity guidance with no diagram or loading placeholder. No identity
change occurred after the token was applied; no clock override or response
injection occurred. Browser diagnostics
were empty and the fixture closed cleanly.

Report: local artifacts/ui14-error-browser-results/ui14-error-browser-expiry.trx.
Request/expiry evidence and the `expired-token-unavailable` page/panel
screenshots are under artifacts/acceptance/runs/20261110-142514-3d269444.
The final command was:

```text
dotnet test Flowbit/tests/Flowbit.BrowserAcceptanceTests/Flowbit.BrowserAcceptanceTests.csproj -c Release --no-build --no-restore --artifacts-path artifacts/ui14-error-browser-build --filter FullyQualifiedName~ExpiredTokenDuringLocalRetryShowsUnavailableWithoutAnIdentityChange --environment FLOWBIT_BROWSER_HEADED=1 --logger "trx;LogFileName=ui14-error-browser-expiry.trx" --results-directory artifacts/ui14-error-browser-results
```

The follow-up browser checks total **23 passed**, with the separate **one
smoke-isolation guard** also passing.

Final documentation validation checked **63** changed relative links/images
and anchors across **12** changed Markdown files, all **four DTO tables and
15 generated OpenAPI fields**, and the history response JSON example, with
zero failures. `git diff --check` passed. The local validation script and
captured OpenAPI are under artifacts/docs-validation.

The full suite was not rerun for these follow-up edits; its preceding results
remain recorded separately above. The authentically aged retention walkthrough
and production-copy migration timing remain outstanding on the deployment
checklist and are outside the agreed feature completion criteria.
