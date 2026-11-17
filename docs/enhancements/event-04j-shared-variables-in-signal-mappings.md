# EVENT-04J: Shared variables in signal mappings

[Parent EVENT-04](event-04-signal-events.md) · [Enhancement backlog](README.md) · [Documentation home](../index.md)

| Field | Value |
| --- | --- |
| Status | Not started |
| Category | New events |
| Effort | M |
| Priority | Not ranked; tenth of eleven in the [EVENT-04 follow-up order](README.md#event-04-follow-up-subtasks) |
| Parent | [EVENT-04](event-04-signal-events.md) |
| Depends on | [EVENT-04](event-04-signal-events.md) (Done) |
| Related | [EVENT-04B](event-04b-signal-throw-and-end-events.md) |

## Summary

A signal mapping may read a shared-variable alias in its default or validation. Writing a shared alias from a signal mapping stays out of the first release.

## Why it adds value

- A validation such as "the new price is within the shared price limit" needs a shared value, which a signal mapping cannot read.
- Writing is rarely wanted: every recipient of one broadcast would write the same shared key.

## First release acceptance criteria

- [ ] A signal mapping's default and validation may read an alias that is bound with `read` or `readWrite` access.
- [ ] A delivery reads those shared values without making the recipients of one broadcast wait for each other on a shared row.
- [ ] The lock-order proof models the delivery of a catch and of a boundary, including these reads, and saving rejects a definition whose keys can be acquired out of order.
- [ ] A delivery still locks one instance and makes no external call. An archived or missing shared key fails only that delivery.
- [ ] A mapping that writes a shared alias is still rejected, with the existing wording.
- [ ] The editor's save validator gives the API's first error for every case in `SignalEventFixtures`.

## Later releases

- Writing a shared alias from a signal mapping.

## Current state

Verified on 2026-11-17: not implemented.

- `ValidateSignalMappings` in [WorkflowDefinitionValidator.cs](../../Flowbit/src/Flowbit.Service/Services/WorkflowDefinitionValidator.cs) rejects a signal mapping that targets a shared alias, and one that reads a shared alias in its default, its validation, or the validation rule of the process variable it targets ([signal configuration](../node-reference.md#signal-configuration)).
- A delivery takes no shared-variable row, and the lock-order proof treats a signal catch and a signal boundary as external resting points.
- The [EVENT-04 decisions](event-04-signal-events.md#open-questions) record why: one broadcast would write the same shared key from every recipient, and a boundary with shared outputs needs a new model in the proof.

## Builds on

- `SharedVariableAccessPlan`, which already derives reads and producer targets from other nodes' mappings, and `SharedVariableTransactionLockOrderValidator`.
- Call activity output validation, which reads a shared alias through the effective values and lists the read in the access plan without locking it.

## Open questions

- Is there a concrete case? Copying the shared value into an instance variable before the wait covers many of them.
- Must a delivery lock the shared row it reads, or is an unlocked read enough, as it is for call output validation?
- Does a boundary delivery start a new segment in the lock-order proof, and which keys does its continuation add?

## Plan

_Not written yet._ Follow [Writing a plan](README.md#writing-a-plan).
