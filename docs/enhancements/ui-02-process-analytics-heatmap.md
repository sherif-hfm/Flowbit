# UI-02: Process analytics and heatmap

[Enhancement backlog](README.md) · [Documentation home](../index.md)

| Field | Value |
| --- | --- |
| Status | Not started |
| Category | Flowbit.Ui feature |
| Effort | M |
| Priority | Not ranked |
| Depends on | [UI-01](ui-01-live-instance-diagram.md) |
| Related | [UI-04](ui-04-task-due-date-and-priority.md), [UI-09](ui-09-saved-views-csv-export.md) |

## Summary

Per workflow family and version: throughput, cycle times, visit counts and durations per node, a bottleneck ranking, path frequency, and deadline breaches, drawn on the diagram as a heatmap.

## Why it adds value

- Process improvement is a core reason to use a workflow engine, and today only instance totals are reported.

## First release acceptance criteria

- [ ] For a selected workflow version and date range, Flowbit.Ui shows per-node visit counts and average and 90th-percentile visit durations from `node_executions`, plus instance cycle time.
- [ ] The same node metrics appear as a heatmap on the [UI-01](ui-01-live-instance-diagram.md) diagram.
- [ ] Only callers with node-execution read permission (`NodeExecution.RequiredRole` or the workflow's `taskAssignmentRoles`) see the metrics.
- [ ] The queries meet an agreed time budget on a large test dataset.

## Later releases

- Flow-level counts and path frequency, once traversed flows are recorded for every workflow.
- Deadline breaches, after [UI-04](ui-04-task-due-date-and-priority.md).
- Throughput trends.

## Current state

Verified on 2026-10-24: not implemented.

- [Overview](../../Flowbit/src/Flowbit.Ui/Components/Pages/Home.razor) shows instance totals, running, My work, and faulted counts, the lifecycle distribution, and recent instances.
- Activity searches individual node executions; no duration, throughput, or bottleneck reporting exists.

## Builds on

- `node_executions`, which records each visit's start, completion, status, and actors.
- The [UI-01](ui-01-live-instance-diagram.md) viewer for the heatmap.
- Note: `sequence_flow_summaries` is written only for definitions that reference `FlowInfo`, so flow counts for every workflow need another source.

## Open questions

- Live SQL aggregation or pre-computed rollups, given table sizes and retention cleanup.
- Flow-level counts for all workflows: always record them, or derive them from history?
- Charting approach in Flowbit.Ui: inline SVG or a charting library.

## Plan

_Not written yet._ Follow [Writing a plan](README.md#writing-a-plan).
