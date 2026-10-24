# EVENT-07: Compensation

[Enhancement backlog](README.md) · [Documentation home](../index.md)

| Field | Value |
| --- | --- |
| Status | Not started |
| Category | New events |
| Effort | L |
| Priority | Not ranked |
| Depends on | None |
| Related | [TASK-01](task-01-call-activity.md), [TASK-04](task-04-email-and-chat-send.md) |

## Summary

Compensation handlers attached to activities that completed, typically REST service tasks, and compensation throw or end events that run those handlers in reverse completion order (the saga pattern).

## Why it adds value

- Undo distributed side effects, such as cancelling a reservation or refunding a payment, when a later step fails or the instance is cancelled.

## First release acceptance criteria

- [ ] A REST service task can name a compensation handler, which is itself a REST call.
- [ ] A compensation throw or end event runs the handlers of completed activities in reverse completion order, each as a durable job with retries and incidents.
- [ ] Each handler receives the outputs its activity produced.
- [ ] Instance detail shows each handler's progress and outcome.

## Later releases

- Compensation triggered by cancellation or `errorEndEvent`.
- Compensation scopes narrower than the whole instance.

## Current state

Verified on 2026-10-24: not implemented.

- [Unsupported capabilities](../bpmn-support.md#unsupported-capabilities) lists compensation.
- [Inspect instances and activity](../ui-guide.md#inspect-instances-and-activity) notes that cancelling an instance cannot undo an already completed external HTTP effect.

## Builds on

- `node_executions` as the ledger of completed visits.
- Durable asynchronous jobs and incidents to run handlers reliably.

## Open questions

- Should a failed handler stop the remaining handlers?
- Without subprocesses, compensation applies to the whole instance; is that enough?

## Plan

_Not written yet._ Follow [Writing a plan](README.md#writing-a-plan).
