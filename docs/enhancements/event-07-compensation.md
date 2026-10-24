# EVENT-07: Compensation

[Enhancement backlog](README.md) · [Documentation home](../index.md)

| Field | Value |
| --- | --- |
| Status | Not started |
| Category | New events |
| Effort | L |
| Priority | Not ranked |
| Related | [TASK-01](task-01-call-activity.md), [TASK-04](task-04-email-and-chat-send.md) |

## Summary

Compensation handlers attached to activities that completed, typically REST service tasks, and compensation throw or end events that run those handlers in reverse completion order (the saga pattern).

## Why it adds value

- Undo distributed side effects, such as cancelling a reservation or refunding a payment, when a later step fails or the instance is cancelled.

## Current state

Verified on 2026-10-24: not implemented.

- [Unsupported capabilities](../bpmn-support.md#unsupported-capabilities) lists compensation.
- [Inspect instances and activity](../ui-guide.md#inspect-instances-and-activity) notes that cancelling an instance cannot undo an already completed external HTTP effect.

## Builds on

- `node_executions` as the ledger of completed visits.
- Durable asynchronous jobs and incidents to run handlers reliably.

## Open questions

- Without subprocesses, compensation scope is the whole instance; is that enough?
- Which data a handler sees: the completed activity's outputs as they were, or current values?
- When a compensation handler fails: incident, retry, or stop compensating?
- Triggers: explicit throw only, or also cancellation and `errorEndEvent`?

## Plan

_Not written yet._ Follow [Writing a plan](README.md#writing-a-plan).
