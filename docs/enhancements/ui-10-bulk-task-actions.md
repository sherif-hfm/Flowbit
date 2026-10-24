# UI-10: Bulk task actions in My work

[Enhancement backlog](README.md) · [Documentation home](../index.md)

| Field | Value |
| --- | --- |
| Status | Not started |
| Category | Flowbit.Ui feature |
| Effort | S–M |
| Priority | Not ranked |
| Depends on | None |
| Related | [UI-09](ui-09-saved-views-csv-export.md) |

## Summary

Select several tasks in My work that offer the same action and take it on each one, with each task authorized separately and a result shown for every item.

## Why it adds value

- High-volume approvers can handle many similar tasks at once.

## First release acceptance criteria

- [ ] My work lets a user select several tasks and take an action that all of them offer and that has no required inputs.
- [ ] Each task is processed with its normal authorization, and a result list shows success or the error for each task.
- [ ] Tasks that need a claim the user does not hold are skipped with a clear reason.

## Later releases

- One shared set of inputs for actions with required inputs.
- A bulk API endpoint with per-item results.

## Current state

Verified on 2026-10-24: not implemented.

- My work takes one action per task ([Use My work and task forms](../ui-guide.md#use-my-work-and-task-forms)).
- [Administrative batches](../ui-guide.md#use-administrative-batches) act on many positions, but they bypass normal task authorization and are for administrators only.

## Builds on

- The existing take-flow endpoint and its authorization checks.
- The administrative-batch result display as a UI precedent.

## Open questions

- How to recognize "the same action" on tasks from different workflow versions: by flow ID, external ID, or name?
- Should claimable tasks be claimed as part of the bulk action?

## Plan

_Not written yet._ Follow [Writing a plan](README.md#writing-a-plan).
