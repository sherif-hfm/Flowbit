# TASK-01: Call activity (reusable subprocess)

[Enhancement backlog](README.md) · [Documentation home](../index.md)

| Field | Value |
| --- | --- |
| Status | Not started |
| Category | New task type |
| Effort | L |
| Priority | Conditional: schedule when a concrete reuse case justifies its lifecycle complexity |
| Depends on | None |
| Related | [TASK-05](task-05-multi-instance-automatic-activities.md), [EVENT-05](event-05-error-code-boundaries.md), [EVENT-07](event-07-compensation.md) |

## Summary

A `callActivity` node starts a child instance of another workflow family and waits until it finishes. The target is chosen by workflow key with a version binding: the published default, the latest published version, or an exact version. Typed input mappings copy parent values into the child's start variables, and typed output mappings copy the child's results back to the parent when the child completes.

## Why it adds value

- Shared fragments, such as a manager approval chain, a KYC check, or a payment step, are modeled once and reused instead of copied into every workflow.
- Diagrams stay smaller, and each fragment is versioned and published on its own.
- It is the most commonly used BPMN capability that Flowbit lacks.

## First release acceptance criteria

- [ ] A `callActivity` node names a target workflow key and starts a child instance of that family's published default version, resolved and saved when the call starts.
- [ ] Typed input mappings fill the child's start variables, and typed output mappings write the child's results to the parent when the child completes, all-or-nothing.
- [ ] The parent token waits while the child runs and advances exactly once when the child completes.
- [ ] Cancelling the parent, or reaching a `terminateEndEvent` in the parent, cancels the running child in the same transaction.
- [ ] A child that faults follows an error boundary attached to the call activity, or faults the parent when none is attached.
- [ ] Calls nested deeper than a configured limit fail with a clear error.
- [ ] Instance detail links a parent to its children and each child to its parent.

## Later releases

- Latest-published and exact version bindings.
- Timer and conditional boundaries on a call activity.
- Multi-instance call activities ([TASK-05](task-05-multi-instance-automatic-activities.md)).
- Error-code matching for child faults ([EVENT-05](event-05-error-code-boundaries.md)).

## Current state

Verified on 2026-10-24: not implemented.

- [Unsupported capabilities](../bpmn-support.md#unsupported-capabilities) lists call activities and reusable subprocess invocation.
- The [database schema](../database-schema.md) has no parent/child relationship between workflow instances.

## Builds on

- Typed start-variable contracts and the typed output mappings used by REST service tasks and message events.
- Published-default version resolution, already used by message starts and timer starts.
- Error boundaries, instance cancellation, and `terminateEndEvent` cancellation for child failure and parent-to-child cascades.
- Start idempotency and business keys, so a retried call never creates a second child.

## Open questions

- How does the saved child version interact with instance version changes on the parent or the child?
- Which identity starts the child (the triggering user or a system actor), and do the child's start roles apply?
- What does the parent's error boundary receive from a faulted child: its fault code and description?
- What call depth limit applies, and where is it configured?
- How does instance search show parent and child relationships?

## Plan

_Not written yet._ Follow [Writing a plan](README.md#writing-a-plan).
