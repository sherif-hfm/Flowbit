# EVENT-04I: Signal boundaries on multi-instance bodies

[Parent EVENT-04](event-04-signal-events.md) · [Enhancement backlog](README.md) · [Documentation home](../index.md)

| Field | Value |
| --- | --- |
| Status | Not started |
| Category | New events |
| Effort | M |
| Priority | Not ranked; ninth of eleven in the [EVENT-04 follow-up order](README.md#event-04-follow-up-subtasks) |
| Parent | [EVENT-04](event-04-signal-events.md) |
| Depends on | [EVENT-04](event-04-signal-events.md) (Done), [TASK-05](task-05-multi-instance-automatic-activities.md) (Done) |
| Related | [TASK-05A](task-05a-multi-instance-core.md), [TASK-06](task-06-multi-instance-automatic-activities-next.md) |

## Summary

A `signalBoundaryEvent` can attach to a multi-instance service, script, or call activity. It applies to the whole body, as timer and conditional boundaries do.

## Why it adds value

- A recall should stop a batch of 1,000 item calls the same way it stops one task.
- Today such a body accepts timer and conditional boundaries but no signal boundary. The workaround is a signal catch on a parallel branch that sets a variable, plus a conditional boundary on the body.

## First release acceptance criteria

- [ ] Both validators accept a signal boundary on an automatic multi-instance body, under the host rules of timer and conditional boundaries and the shared limit of eight.
- [ ] The body subscribes when it is entered, before its first job. Every exit of the body ends the subscription in the same transaction: completion, an error boundary, another interrupting boundary, and cancellation.
- [ ] An interrupting delivery stops the body: unfinished items are cancelled and fenced, late item results are rejected, child calls are cancelled, and no aggregate is written, as when a timer or conditional boundary interrupts.
- [ ] A non-interrupting delivery adds a branch within the active-token limit while the body keeps running.
- [ ] Version changes treat an attached signal boundary on an open body as they treat its other boundaries.
- [ ] The editor offers **+ Add signal boundary event** on these hosts.
- [ ] PostgreSQL tests cover a delivery racing an item's completion and the body's completion, and real-browser verification with a running Worker is complete.

## Later releases

- Signal boundaries on single items of a body, if per-item boundaries are added ([TASK-06](task-06-multi-instance-automatic-activities-next.md)).

## Current state

Verified on 2026-11-17: not implemented.

- The signal boundary's host check in [WorkflowDefinitionValidator.cs](../../Flowbit/src/Flowbit.Service/Services/WorkflowDefinitionValidator.cs) calls `ValidateObservableBoundary` with `allowAutomaticBodies: false`, which refuses every automatic multi-instance body. The editor leaves the add button out on such a host.
- Timer and conditional boundaries on a body are supported ([multi-instance activities](../bpmn-support.md#multi-instance-service-script-and-call-activities)). Their lifetime runs through the `IAutomaticMultiInstanceLifecycle` callbacks that [TASK-05A](task-05a-multi-instance-core.md) owns.
- A signal delivery interrupts its host through `InterruptBoundaryHostAsync`, the sequence it shares with the conditional capture.

## Builds on

- The body's boundary lifecycle from TASK-05A: subscription on entry, cancellation on every exit, and fenced late results.
- `InterruptBoundaryHostAsync` and the delivery's token-limit check.
- `EnsureSignalSubscriptionsAsync`, which already subscribes a host's signal boundaries on entry.

## Open questions

- Does `InterruptBoundaryHostAsync` already settle an automatic body completely, or must the delivery go through the coordinator's own stop path?
- A delivery locks the instance, then the subscription, then the delivery row. Where do the body and item locks fit in that order?
- Is there a concrete case that the workaround does not cover? If not, this item can wait.

## Plan

_Not written yet._ Follow [Writing a plan](README.md#writing-a-plan).
