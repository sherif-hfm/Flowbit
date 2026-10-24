# UI-04: Task due date and priority

[Enhancement backlog](README.md) · [Documentation home](../index.md)

| Field | Value |
| --- | --- |
| Status | Not started |
| Category | Flowbit.Ui feature (with engine changes) |
| Effort | M |
| Priority | Recommended order #3 |
| Depends on | None |
| Related | [UI-12](ui-12-task-escalation.md), [EVENT-01](event-01-expression-timers.md), [EVENT-08](event-08-business-calendars.md), [UI-02](ui-02-process-analytics-heatmap.md), [UI-13](ui-13-notification-center.md) |

## Summary

`userTask` gains an optional `dueDate` and `priority`, evaluated once when the work item is created and saved on it. My work shows due dates with overdue and due-soon badges and can sort and filter by due date and priority. Escalation is a separate item ([UI-12](ui-12-task-escalation.md)).

## Why it adds value

- Deadlines become visible to the people doing the work instead of existing only as timers in the model.
- The inbox APIs return them too, so applications that embed Flowbit can sort their own worklists by them.

## First release acceptance criteria

- [ ] A `userTask` accepts an optional `dueDate` (an expression giving a date-time, or a duration counted from task entry) and an optional `priority` (an expression giving a number).
- [ ] Both are evaluated once when the work item is created and saved on it; multi-instance children get the values of their execution.
- [ ] Inbox, task-management, and task APIs return the due date and priority, and the inbox can sort and filter by both.
- [ ] My work shows the due date with overdue and due-soon badges.
- [ ] Definitions and tasks without these fields behave exactly as before.

## Later releases

- An "Overdue tasks" card on Overview.
- Durations counted in working time ([EVENT-08](event-08-business-calendars.md)).

## Current state

Verified on 2026-10-24: not implemented.

- `DueAt` and `Priority` exist only on durable jobs ([JobModels.cs](../../Flowbit/src/Flowbit.Service/Models/JobModels.cs)), not on user tasks.
- Inbox sort fields are `userTaskId`, `instanceId`, `taskCreatedAt`, `taskUpdatedAt`, `instanceCreatedAt`, and `instanceUpdatedAt`.

## Builds on

- Assignee-expression evaluation on task entry: the same evaluate-once-and-save pattern.
- Inbox sorting, filtering, and paging in PostgreSQL.
- Note: overdue is computed when lists are read, so the first release needs no Worker.

## Open questions

- When the expression fails, should the transition fail, or should the task be created without a due date and a warning logged, as assignee expressions do?
- Should the due date be recomputed when a source variable changes?
- How is the due-soon threshold configured?
- Migration: new `user_tasks` columns and the indexes needed for sorting.

## Plan

_Not written yet._ Follow [Writing a plan](README.md#writing-a-plan).
