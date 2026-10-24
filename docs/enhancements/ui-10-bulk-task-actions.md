# UI-10: Bulk task actions in My work

[Enhancement backlog](README.md) · [Documentation home](../index.md)

| Field | Value |
| --- | --- |
| Status | Not started |
| Category | Flowbit.Ui feature |
| Effort | S–M |
| Priority | Not ranked |
| Related | [UI-09](ui-09-saved-views-csv-export.md) |

## Summary

Select several tasks in My work that offer the same action and take it on each one, with each task authorized separately and a result shown for every item.

## Why it adds value

- High-volume approvers can handle many similar tasks at once.

## Current state

Verified on 2026-10-24: not implemented.

- My work takes one action per task ([Use My work and task forms](../ui-guide.md#use-my-work-and-task-forms)).
- [Administrative batches](../ui-guide.md#use-administrative-batches) act on many positions, but they bypass normal task authorization and are for administrators only.

## Builds on

- The existing take-flow endpoint and its authorization checks.
- The administrative-batch result display as a UI precedent.

## Open questions

- Allow only actions without required inputs, or collect one shared set of inputs?
- Loop over existing endpoints in the UI, or add a bulk endpoint that returns per-item results?
- Tasks that require a claim: claim as part of the bulk action?

## Plan

_Not written yet._ Follow [Writing a plan](README.md#writing-a-plan).
