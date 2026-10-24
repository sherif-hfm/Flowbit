# UI-04: Task due date and priority

[Enhancement backlog](README.md) · [Documentation home](../index.md)

| Field | Value |
| --- | --- |
| Status | Not started |
| Category | Flowbit.Ui feature (with engine changes) |
| Effort | M |
| Priority | Recommended order #2, with [EVENT-01](event-01-expression-timers-business-calendars.md) |
| Related | [EVENT-01](event-01-expression-timers-business-calendars.md), [UI-05](ui-05-notification-center-live-refresh.md), [UI-02](ui-02-process-analytics-heatmap.md) |

## Summary

`userTask` gains an optional `dueDate` (an expression or a duration, calendar-aware with [EVENT-01](event-01-expression-timers-business-calendars.md)) and `priority` (an expression), saved on the work item when it is created. My work shows overdue and due-soon badges and can sort and filter by due date and priority, and Overview gains an "Overdue tasks" card. An optional escalation policy reassigns, adds roles, or notifies when a task becomes overdue.

## Why it adds value

- Deadlines become visible to the people doing the work instead of existing only as timers in the model.

## Current state

Verified on 2026-10-24: not implemented.

- `DueAt` and `Priority` exist only on durable jobs ([JobModels.cs](../../Flowbit/src/Flowbit.Service/Models/JobModels.cs)), not on user tasks.
- Inbox sort fields are `userTaskId`, `instanceId`, `taskCreatedAt`, `taskUpdatedAt`, `instanceCreatedAt`, and `instanceUpdatedAt`.

## Builds on

- Assignee-expression evaluation on task entry, the same evaluate-once-and-save pattern.
- Inbox sorting, filtering, and paging in PostgreSQL.
- Timer boundaries, task role management, and assignment services for escalation actions.

## Open questions

- Recompute the due date when a source variable changes, or keep the value saved on entry?
- Escalation as a task policy, or as a new non-interrupting boundary action?
- Multi-instance tasks: a due date per child, or one for the parent?
- Migration: new `user_tasks` columns and the indexes needed for sorting.

## Plan

_Not written yet._ Follow [Writing a plan](README.md#writing-a-plan).
