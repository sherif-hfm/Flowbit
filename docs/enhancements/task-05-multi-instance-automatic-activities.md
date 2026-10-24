# TASK-05: Multi-instance service, script, and call activities

[Enhancement backlog](README.md) · [Documentation home](../index.md)

| Field | Value |
| --- | --- |
| Status | Not started |
| Category | New task type |
| Effort | M–L |
| Priority | Not ranked |
| Related | [TASK-01](task-01-call-activity.md), [TASK-02](task-02-external-task.md) |

## Summary

Allow `multiInstance` on `serviceTask`, `scriptTask`, and the proposed `callActivity` ([TASK-01](task-01-call-activity.md)) so one node processes every item of a collection, in parallel or in sequence, and produces a result collection. Examples: notify each supplier, or check each order line.

## Why it adds value

- Batch integrations without hand-built loops of gateways and counters.

## Current state

Verified on 2026-10-24: not implemented.

- [Human work and multi-instance tasks](../bpmn-support.md#human-work-and-multi-instance-tasks) states that multi-instance configuration is supported on `userTask` only.
- [Unsupported capabilities](../bpmn-support.md#unsupported-capabilities) lists multi-instance behavior on other activity types.

## Builds on

- Multi-instance executions: one parent token, child items, ordered result collections, and the `Workflow.MultiInstance.MaxInstances` limit.
- Durable asynchronous jobs for each REST item.

## Open questions

- How each child sees its item and index, and where it writes its local result.
- Failure semantics: one failed item fails the node, the others continue and errors are collected, or a boundary handles it.
- Concurrency limits for parallel REST calls.
- Completion rules for automatic children: always all items, or an early-stop condition?

## Plan

_Not written yet._ Follow [Writing a plan](README.md#writing-a-plan).
