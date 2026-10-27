# UI-16: Overdue work and due-date follow-ups

[Enhancement backlog](README.md) · [Documentation home](../index.md)

| Field | Value |
| --- | --- |
| Status | Not started |
| Category | Flowbit.Ui feature (with engine changes) |
| Effort | M |
| Priority | Not ranked |
| Depends on | [UI-04](ui-04-task-due-date-and-priority.md) |
| Related | [UI-12](ui-12-task-escalation.md), [EVENT-08](event-08-business-calendars.md), [UI-13](ui-13-notification-center.md) |

## Summary

The due-date scope left after [UI-04](ui-04-task-due-date-and-priority.md): an **Overdue tasks** card on Overview, sorting and filtering by due date and priority in Task management and task distribution, and optional recomputation of a work item's due date and priority when a variable they read changes.

## Why it adds value

- People and managers see overdue work as soon as they open Flowbit.Ui, instead of only in My work's own sort and filters.
- Managers who assign work can find late and urgent tasks across the workflows they manage, and external distributors can route them first.
- A due date that follows the data, such as a contract end date corrected after the task started, stays accurate without restarting the task.

## First release acceptance criteria

- [ ] Overview shows an **Overdue tasks** card with the number of overdue tasks the caller can see in My work, linking to My work filtered to overdue tasks.
- [ ] Task management and task distribution sort by `dueAt` and `priority` and filter by due status, due range, and priority range, with the same rules as the inbox, in PostgreSQL before counting and paging.
- [ ] Definitions and tasks without due dates or priorities behave exactly as before.
- [ ] Verified in a real browser at desktop and phone widths.

## Later releases

- Recomputing a work item's due date and priority when a variable they read changes, opted into per task.
- An overdue count for managers across the workflows they manage.

## Current state

Verified on 2026-10-27: not implemented.

- [UI-04](ui-04-task-due-date-and-priority.md) saves `dueAt` and `priority` on each work item when it is created and returns them with a `dueStatus` from task, inbox, task-management, and task-distribution APIs ([due date and priority](../node-reference.md#due-date-and-priority)).
- Only the inbox sorts and filters by them. The task-management and task-distribution searches have no sort parameter, and their pages show the **Due** column without sorting or filtering.
- Overview's My work list shows the four most recent tasks with an **Overdue** or **Due soon** badge; there is no overdue count.
- Saved values never change after the work item is created.

## Builds on

- The `(Status, DueAt, Id)` index on `user_tasks` and the inbox's due filters.
- The `UserTasks.DueSoonThreshold` engine setting and the API-computed `dueStatus`.
- The conditional events' dependency tracking for variable writes, which recomputation could reuse.

## Open questions

- Should the Overview card count only tasks the caller can act on, or every task visible in My work?
- Should recomputation follow the same dependency rules as conditional events, and what happens to a due date that was already passed?
- Does task distribution need the same filters, or is sorting enough for external routers?

## Plan

_Not written yet._ Follow [Writing a plan](README.md#writing-a-plan).
