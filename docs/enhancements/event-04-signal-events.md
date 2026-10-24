# EVENT-04: Signal events

[Enhancement backlog](README.md) · [Documentation home](../index.md)

| Field | Value |
| --- | --- |
| Status | Not started |
| Category | New events |
| Effort | M–L |
| Priority | Not ranked |
| Depends on | None |
| Related | [EVENT-02](event-02-message-correlation-by-business-key.md), [EVENT-03](event-03-event-based-gateway.md) |

## Summary

Signal start, intermediate catch, boundary, intermediate throw, and end events. Throwing a signal by name, from a workflow or through the API, reaches every subscribed instance, optionally limited to one workflow family or business key.

## Why it adds value

- Broadcast situations such as "policy changed", "product recall", or "fiscal year closed", where many open instances must react at once.

## First release acceptance criteria

- [ ] Intermediate signal catch and signal boundary events subscribe by signal name while they are active.
- [ ] An authenticated API call broadcasts a signal by name, optionally limited to one workflow family.
- [ ] The Worker delivers the signal through paged durable jobs; every subscription that existed when the signal was thrown receives it once, and delivery progress is visible.
- [ ] The signal payload maps into each receiving instance through typed mappings.

## Later releases

- Signal start, intermediate throw, and end events.
- Limiting a signal to one business key.

## Current state

Verified on 2026-10-24: not implemented.

- [Unsupported capabilities](../bpmn-support.md#unsupported-capabilities) lists signal events.

## Builds on

- Durable, paged Worker fan-out, as administrative batches already do with `administrativeBatchPrepare` and `administrativeBatchExecute` jobs.
- The subscription and fencing patterns of conditional boundaries.
- Message starts as the model for instances started by the system.

## Open questions

- What happens to an instance that subscribes while a fan-out is in progress?
- Which machine-client scopes allow throwing signals?
- Fan-out limits.

## Plan

_Not written yet._ Follow [Writing a plan](README.md#writing-a-plan).
