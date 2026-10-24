# UI-12: Task escalation

[Enhancement backlog](README.md) · [Documentation home](../index.md)

| Field | Value |
| --- | --- |
| Status | Not started |
| Category | Human work (engine changes) |
| Effort | M–L |
| Priority | Next tier: deadline and timer follow-up 2 |
| Depends on | [UI-04](ui-04-task-due-date-and-priority.md) |
| Related | [UI-13](ui-13-notification-center.md), [EVENT-08](event-08-business-calendars.md) |

## Summary

A `userTask` escalation policy that runs when the task's due date passes: reassign the task to an actor from an expression, add roles to its saved role policy, or both, with an audit record.

## Why it adds value

- Overdue work moves to someone who can act, without interrupting the task. Today escalating means an interrupting timer boundary that routes to a different task, which ends the original work item.

## First release acceptance criteria

- [ ] A `userTask` with a due date ([UI-04](ui-04-task-due-date-and-priority.md)) can declare an escalation that reassigns it to an actor expression, adds roles, or both, when the due date passes.
- [ ] The escalation runs once as a durable Worker job and is cancelled if the task completes first.
- [ ] Each escalation writes an audit history row with system attribution and an escalation authority, like other ownership changes.
- [ ] Reassignment clears an existing claim, as manager reassignment does.
- [ ] Version switching with a pending escalation is either supported or explicitly blocked.

## Later releases

- Multi-step escalation chains.
- Notifying people about an escalation ([UI-13](ui-13-notification-center.md)).
- Escalation for multi-instance children.

## Current state

Verified on 2026-10-24: not implemented.

- Timer boundaries can interrupt a task or start a sibling branch, but nothing changes a waiting task's assignee or roles automatically. Only authorized managers can do that, through Task management assignment and **Edit roles** ([Manage assignments and roles](../ui-guide.md#manage-assignments-and-roles)).

## Builds on

- The due dates saved by [UI-04](ui-04-task-due-date-and-priority.md).
- Durable Worker jobs with activation fences.
- Manager assignment, which records `taskAssignment` history, and `IUserTaskRoleManagementService`, which replaces a task's saved role policy and records `taskRolesChanged`.

## Open questions

- A task-level policy, or a new kind of non-interrupting timer boundary action?
- Interaction with delegation and with `requiresAssignment` tasks.
- Can the actor expression read the current assignee?

## Plan

_Not written yet._ Follow [Writing a plan](README.md#writing-a-plan).
