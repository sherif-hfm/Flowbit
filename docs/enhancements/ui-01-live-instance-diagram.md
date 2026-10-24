# UI-01: Live instance diagram

[Enhancement backlog](README.md) · [Documentation home](../index.md)

| Field | Value |
| --- | --- |
| Status | Not started |
| Category | Flowbit.Ui feature |
| Effort | M |
| Priority | Recommended order #1 |
| Depends on | None |
| Related | [UI-02](ui-02-process-analytics-heatmap.md) and [UI-07](ui-07-version-compare.md), which reuse this viewer; [EVENT-06](event-06-link-events.md) |

## Summary

Show the instance's exact workflow version as a read-only diagram on instance detail, highlighting where the instance is and what work is waiting. Selecting a node links to its execution detail. The path the instance took is added in later releases, once its evidence is complete.

## Why it adds value

- Operators and end users see where an instance is at a glance. Today the instance page has only lists.

## First release acceptance criteria

- [ ] Instance detail shows the instance's exact workflow version as a read-only diagram, rendered by the shared editor assets.
- [ ] Active execution positions and waiting work items are highlighted.
- [ ] Selecting a highlighted node links to its node execution detail and, for a user task, to the task.
- [ ] No historical path is drawn.
- [ ] Verified in a real browser at desktop and phone widths, with no console errors.

## Later releases

- Visited nodes from `node_executions`, with a notice that visits before that table's deployment, and pruned history, are missing.
- Flows taken, once every traversed flow is recorded for all workflows.
- Markers for open incidents.

## Current state

Verified on 2026-10-24: not implemented.

- Instance detail renders execution positions, variables, history, gateway scopes, multi-instance results, and other lists ([`Components/Shared/InstanceDetails/`](../../Flowbit/src/Flowbit.Ui/Components/Shared/InstanceDetails/)), but no diagram.
- The shared editor has no read-only or viewer mode ([flowbit-editor.js](../../Flowbit/src/Flowbit.Ui/wwwroot/editor/flowbit-editor.js) has no `readOnly` handling).
- Path evidence is incomplete today: `node_executions` covers visits only from its deployment onward and retention can prune it; complete flow-traversal evidence exists only for workflows that use `FlowInfo`; and `instance_history` is documented as not a complete ledger.

## Builds on

- `FlowbitEditor.mount(container, options)` and the shared rendering pipeline (`renderLanes`, `renderEdges`, `renderNodes`).
- Execution positions already returned by instance detail, and node execution detail pages for the links.

## Open questions

- A read-only option on the editor, or a separate viewer entry point in the same asset folder? AGENTS.md forbids duplicating editor markup, styles, or rendering in Razor.
- The overlay data contract: can the existing instance detail response supply it, or is a new call needed?
- Authorization: instance detail does not apply personal task visibility; may every viewer follow the links to node execution detail?

## Plan

_Not written yet._ Follow [Writing a plan](README.md#writing-a-plan).
