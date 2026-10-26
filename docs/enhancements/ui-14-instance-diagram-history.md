# UI-14: Instance diagram history

[Enhancement backlog](README.md) · [Documentation home](../index.md)

| Field | Value |
| --- | --- |
| Status | Not started |
| Category | Flowbit.Ui feature |
| Effort | M |
| Priority | Not ranked |
| Depends on | [UI-01](ui-01-live-instance-diagram.md) |
| Related | [UI-02](ui-02-process-analytics-heatmap.md), which reads the same `node_executions` rows |

## Summary

Add the path an instance took to the [UI-01](ui-01-live-instance-diagram.md) instance diagram: the nodes it visited, the flows it took, and its open incidents. UI-01 recorded this as its later-release scope.

## Why it adds value

- Operators can see how an instance reached its current position without reading history rows.

## First release acceptance criteria

- [ ] The instance diagram marks visited nodes from `node_executions`, distinct from current and final positions.
- [ ] It marks the flows taken, counting a multi-instance task's entry flow once per multi-instance execution.
- [ ] A notice explains when an instance's visits are incomplete: it started before the `AddNodeExecutions` migration, or retention removed its visits.
- [ ] Verified in a real browser at desktop and phone widths, with no console errors.

## Later releases

- Markers for open incidents.

## Current state

Verified on 2026-10-26: not implemented. The UI-01 diagram marks only current positions, or final positions once the instance has finished.

## Builds on

- The UI-01 viewer: `FlowbitEditor.mount(container, { mode: "viewer" })` and its node overlay, which would gain visited-node and flow markers.
- [`node_executions`](../database-schema.md#node_executions), which records each committed visit with its entry, selected, and exit flows for every workflow, whether or not it uses `FlowInfo`. On 2026-10-24, a comparison of ten workflow shapes against PostgreSQL found the same traversals in `node_executions` with and without `FlowInfo`. Each visit's `EnteredViaFlowId` is one traversal. Multi-instance child visits all repeat their task's entry flow. The entry flow is empty where no sequence flow was taken: start events, boundary events, extra tokens created at a fork, Complex gateway resets, and reactivation.
- Visits that ended before the `AddNodeExecutions` migration were not backfilled; only work open at that moment was seeded and marked `IsCutoverSeeded`. [Retention](../ui-guide.md#manage-retention) of node activity can delete the visits of finished instances but never of running ones.

## Open questions

- Node executions are role-scoped (`NodeExecution.RequiredRole` or the version's `taskAssignmentRoles`). Is the path shown only to those callers, or does it need its own read model?
- How are visits and flow counts loaded for long-running instances with many visits: a bounded search, or a summary endpoint?

## Plan

_Not written yet._ Follow [Writing a plan](README.md#writing-a-plan).
