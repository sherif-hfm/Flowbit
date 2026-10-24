# UI-01: Live instance diagram

[Enhancement backlog](README.md) · [Documentation home](../index.md)

| Field | Value |
| --- | --- |
| Status | Not started |
| Category | Flowbit.Ui feature |
| Effort | M |
| Priority | Recommended order #1 |
| Related | [UI-02](ui-02-process-analytics-heatmap.md), [UI-07](ui-07-version-compare.md), [EVENT-06](event-06-link-events.md) |

## Summary

Show the instance's exact workflow version as a read-only diagram on instance detail, highlighting active positions, the path taken, waiting tasks, and incidents. Selecting a node lists its visits and links to node execution detail.

## Why it adds value

- Operators and end users see where an instance is and how it got there. Today the instance page has only lists.

## Current state

Verified on 2026-10-24: not implemented.

- Instance detail renders execution positions, variables, history, gateway scopes, multi-instance results, and other lists ([`Components/Shared/InstanceDetails/`](../../Flowbit/src/Flowbit.Ui/Components/Shared/InstanceDetails/)), but no diagram.
- The shared editor has no read-only or viewer mode ([flowbit-editor.js](../../Flowbit/src/Flowbit.Ui/wwwroot/editor/flowbit-editor.js) has no `readOnly` handling).

## Builds on

- `FlowbitEditor.mount(container, options)` and the shared rendering pipeline (`renderLanes`, `renderEdges`, `renderNodes`).
- Execution positions already returned by instance detail, and `node_executions` for visit history.

## Open questions

- A read-only option on the editor, or a separate viewer entry point in the same asset folder? AGENTS.md forbids duplicating editor markup, styles, or rendering in Razor.
- The overlay data contract: one API call for positions, visit counts, and incidents?
- Authorization: instance detail does not apply personal task visibility; may every viewer see node visit details?
- Layout at narrow widths, and reuse for [UI-02](ui-02-process-analytics-heatmap.md) and [UI-07](ui-07-version-compare.md).

## Plan

_Not written yet._ Follow [Writing a plan](README.md#writing-a-plan).
