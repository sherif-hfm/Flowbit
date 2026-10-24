# TASK-01: Call activity (reusable subprocess)

[Enhancement backlog](README.md) · [Documentation home](../index.md)

| Field | Value |
| --- | --- |
| Status | Not started |
| Category | New task type |
| Effort | L |
| Priority | Recommended order #3 |
| Related | [TASK-05](task-05-multi-instance-automatic-activities.md), [EVENT-05](event-05-error-code-boundaries.md), [EVENT-07](event-07-compensation.md) |

## Summary

A `callActivity` node starts a child instance of another workflow family and waits until it finishes. The target is chosen by workflow key with a version binding: the published default, the latest published version, or an exact version. Typed input mappings copy parent values into the child's start variables, and typed output mappings copy the child's results back to the parent when the child completes.

## Why it adds value

- Shared fragments, such as a manager approval chain, a KYC check, or a payment step, are modeled once and reused instead of copied into every workflow.
- Diagrams stay smaller, and each fragment is versioned and published on its own.
- It is the most commonly used BPMN capability that Flowbit lacks.

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

- Is the child version resolved and saved when the call starts, and how does that interact with instance version changes on the parent or the child?
- Does a faulted child fault the parent, follow an error boundary on the call activity, or open an incident?
- Do parent cancellation and `terminateEndEvent` cancel the child in the same transaction?
- Which identity starts the child (the triggering user or a system actor), and do the child's start roles apply?
- Can timer, conditional, and error boundaries attach to a call activity, and can it be multi-instance ([TASK-05](task-05-multi-instance-automatic-activities.md))?
- How do instance detail and instance search link a parent and its children?

## Plan

_Not written yet._ Follow [Writing a plan](README.md#writing-a-plan).
