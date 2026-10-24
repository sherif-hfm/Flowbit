# EVENT-04: Signal events

[Enhancement backlog](README.md) · [Documentation home](../index.md)

| Field | Value |
| --- | --- |
| Status | Not started |
| Category | New events |
| Effort | M–L |
| Priority | Not ranked |
| Related | [EVENT-02](event-02-message-correlation-by-business-key.md), [EVENT-03](event-03-event-based-gateway.md) |

## Summary

Signal start, intermediate catch, boundary, intermediate throw, and end events. Throwing a signal by name, from a workflow or through the API, reaches every subscribed instance, optionally limited to one workflow family or business key.

## Why it adds value

- Broadcast situations such as "policy changed", "product recall", or "fiscal year closed", where many open instances must react at once.

## Current state

Verified on 2026-10-24: not implemented.

- [Unsupported capabilities](../bpmn-support.md#unsupported-capabilities) lists signal events.

## Builds on

- Durable, paged Worker fan-out, as administrative batches already do with `administrativeBatchPrepare` and `administrativeBatchExecute` jobs.
- The subscription and fencing patterns of conditional boundaries.
- Message starts as the model for instances started by the system.

## Open questions

- Delivery guarantees: are subscribers captured at throw time, and what happens to an instance that subscribes during fan-out?
- Authorization for throwing signals through the API, including machine-client scopes.
- Mapping the signal payload into each receiving instance.
- Fan-out limits and progress visibility in Flowbit.Ui.

## Plan

_Not written yet._ Follow [Writing a plan](README.md#writing-a-plan).
