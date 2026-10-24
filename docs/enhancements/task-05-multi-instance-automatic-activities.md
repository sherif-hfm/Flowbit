# TASK-05: Multi-instance service, script, and call activities

[Enhancement backlog](README.md) · [Documentation home](../index.md)

| Field | Value |
| --- | --- |
| Status | Not started |
| Category | New task type |
| Effort | M–L |
| Priority | Not ranked |
| Depends on | None |
| Related | [TASK-01](task-01-call-activity.md) |

## Summary

Allow `multiInstance` on `serviceTask`, `scriptTask`, and the proposed `callActivity` ([TASK-01](task-01-call-activity.md)) so one node processes every item of a collection, in parallel or in sequence, and produces a result collection. Examples: notify each supplier, or check each order line.

## Why it adds value

- Batch integrations without hand-built loops of gateways and counters.

## First release acceptance criteria

- [ ] `multiInstance` with a `collection` source works on `serviceTask` and `scriptTask`, in parallel and sequential modes.
- [ ] Each child can read its item and index, and its result lands in the ordered result collection.
- [ ] Each REST child runs as its own durable job with retries, and a configurable limit bounds how many run at once.
- [ ] A failed child follows the node's error boundary and cancels unfinished children; without a boundary, it opens an incident and the parent waits.
- [ ] Fan-out obeys `Workflow.MultiInstance.MaxInstances`.

## Later releases

- The `cardinality` source and early-completion conditions.
- Multi-instance call activities, after [TASK-01](task-01-call-activity.md).

## Current state

Verified on 2026-10-24: not implemented.

- [Human work and multi-instance tasks](../bpmn-support.md#human-work-and-multi-instance-tasks) states that multi-instance configuration is supported on `userTask` only.
- [Unsupported capabilities](../bpmn-support.md#unsupported-capabilities) lists multi-instance behavior on other activity types.

## Builds on

- Multi-instance executions: one parent token, child items, ordered result collections, and the `Workflow.MultiInstance.MaxInstances` limit.
- Durable asynchronous jobs for each REST item.

## Open questions

- Names for the item and index in expressions and scripts.
- Where a child writes a local result versus a process variable.
- The default concurrency limit.

## Plan

_Not written yet._ Follow [Writing a plan](README.md#writing-a-plan).
