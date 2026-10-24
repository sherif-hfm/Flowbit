# UI-02: Process analytics and heatmap

[Enhancement backlog](README.md) · [Documentation home](../index.md)

| Field | Value |
| --- | --- |
| Status | Not started |
| Category | Flowbit.Ui feature |
| Effort | M |
| Priority | Not ranked |
| Related | [UI-01](ui-01-live-instance-diagram.md), [UI-04](ui-04-task-due-date-and-priority.md), [UI-09](ui-09-saved-views-csv-export.md) |

## Summary

Per workflow family and version: throughput, cycle-time percentiles, average waiting and active time per node, a bottleneck ranking, path frequency, and deadline breaches, drawn on the diagram as a heatmap.

## Why it adds value

- Process improvement is a core reason to use a workflow engine, and today only instance totals are reported.

## Current state

Verified on 2026-10-24: not implemented.

- [Overview](../../Flowbit/src/Flowbit.Ui/Components/Pages/Home.razor) shows instance totals, running, My work, and faulted counts, the lifecycle distribution, and recent instances.
- Activity searches individual node executions; no duration, throughput, or bottleneck reporting exists.

## Builds on

- `node_executions`, which records each visit's start, completion, status, and actors.
- The [UI-01](ui-01-live-instance-diagram.md) viewer for the diagram overlay.
- Note: `sequence_flow_summaries` is written only for definitions that reference `FlowInfo`, so flow counts for every workflow need another source.

## Open questions

- Live SQL aggregation or pre-computed rollups, given table sizes and retention cleanup.
- Flow-level counts for all workflows: always record them, or derive them from history?
- Authorization: reuse the node-execution visibility rule (`NodeExecution.RequiredRole` or `taskAssignmentRoles`)?
- Charting approach in Flowbit.Ui: inline SVG or a charting library.

## Plan

_Not written yet._ Follow [Writing a plan](README.md#writing-a-plan).
