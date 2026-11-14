# TASK-06: Automatic multi-instance after the first release

[Enhancement backlog](README.md) · [Documentation home](../index.md)

| Field | Value |
| --- | --- |
| Status | Not started |
| Category | New task type |
| Effort | L |
| Priority | Not ranked |
| Depends on | [TASK-05](task-05-multi-instance-automatic-activities.md) (Done) |
| Related | [TASK-05A](task-05a-multi-instance-core.md), [TASK-05B](task-05b-multi-instance-service-activities.md), [TASK-05C](task-05c-multi-instance-script-activities.md), [TASK-05D](task-05d-multi-instance-call-activities.md) |

## Summary

The scope that [TASK-05](task-05-multi-instance-automatic-activities.md) kept out of its first release of multi-instance service, script, and call activities, plus the scale and rollback findings that release accepted with a reason. Each item needs its own plan, with acceptance criteria and evidence, before implementation starts; this page only keeps the list together.

## Why it adds value

- Bodies that cannot finish today, because one item keeps failing and no boundary applies, could skip or replace that item instead of waiting for cancellation.
- One instance commits its items one transaction at a time, which limits a 1,000-item body to a few items per second on the scale-run machine.
- Reverting to binaries older than TASK-05 while a body is open breaks that body silently; today only the documented drain procedure prevents that.

## Candidate items

Deferred by the first-release plan ([TASK-05, Later releases](task-05-multi-instance-automatic-activities.md#later-releases)):

- The `cardinality` source, quorum and early-completion conditions, and selectable or manual parent interrupt flows for automatic bodies.
- Streaming collections, direct per-item commits to ordinary parent-instance variables, and implicit reductions over earlier items' outputs.
- Per-item boundaries, compensation, and selective item skipping, cancellation, or replacement.
- Live changes to a running body's concurrency or input snapshot, and in-place version switching of an open body.
- Further call version bindings, item-dependent call targets, replacing a terminal failed child on retry, and per-item subprocess cancellation.

Accepted at release with a reason ([TASK-05 release readiness](task-05-multi-instance-automatic-activities.md#release-readiness)):

- Batching item stage, save, and publication work so one instance can commit more than about 4–5 items per second.
- Reading only the item and the referenced inputs when an item stages, instead of the whole body snapshot, and scoping the dispatch progress read to one body.
- A guard that stops binaries older than TASK-05 from leasing automatic-body jobs or republishing body definitions without `multiInstance` ([deployment](../deployment.md#automatic-multi-instance-activities)).

## Current state

Verified on 2026-11-14: none of these items is implemented. The first release and its limits are described in [BPMN support](../bpmn-support.md), the [node reference](../node-reference.md#automatic-multi-instance-properties), and [deployment](../deployment.md#automatic-multi-instance-activities).

## Open questions

Which items to plan first, and whether the scale items should wait for production measurements.
